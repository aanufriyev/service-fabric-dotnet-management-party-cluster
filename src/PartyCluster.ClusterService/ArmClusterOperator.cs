﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace PartyCluster.ClusterService
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Fabric;
    using System.Fabric.Description;
    using System.IO;
    using System.Threading.Tasks;
    using Microsoft.Azure;
    using Microsoft.Azure.Management.Resources;
    using Microsoft.Azure.Management.Resources.Models;
    using Microsoft.IdentityModel.Clients.ActiveDirectory;
    using PartyCluster.Common;
    using PartyCluster.Domain;

    internal class ArmClusterOperator : IClusterOperator
    {
        private ArmClusterOperatorSettings settings;
        private string armTemplate;
        private string armParameters;

        public ArmClusterOperator(StatefulServiceContext serviceContext)
        {
            ConfigurationPackage configPackage = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            DataPackage dataPackage = serviceContext.CodePackageActivationContext.GetDataPackageObject("Data");

            this.UpdateClusterOperatorSettings(configPackage.Settings);
            this.UpdateArmTemplateContent(dataPackage.Path);
            this.UpdateArmParameterContent(dataPackage.Path);

            serviceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent
                += this.CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            serviceContext.CodePackageActivationContext.DataPackageModifiedEvent
                += this.CodePackageActivationContext_DataPackageModifiedEvent;
        }

        /// <summary>
        /// Initiates creation of a new cluster.
        /// </summary>
        /// <remarks>
        /// If a cluster with the given domain could not be created, an exception should be thrown indicating the failure reason.
        /// </remarks>
        /// <param name="name">A unique name for the cluster.</param>
        /// <returns>The FQDN of the new cluster.</returns>
        public async Task<string> CreateClusterAsync(string name, IEnumerable<int> ports)
        {
            string token = await this.GetAuthorizationTokenAsync();
            TokenCloudCredentials credential = new TokenCloudCredentials(this.settings.SubscriptionID.ToUnsecureString(), token);

            string rgStatus = await this.CreateResourceGroupAsync(credential, name);

            if (rgStatus == "Exists")
            {
                throw new System.InvalidOperationException(
                    "ResourceGroup/Cluster already exists. Please try passing a different name, or delete the ResourceGroup/Cluster first.");
            }

            string templateContent = this.armTemplate;
            string parameterContent = this.armParameters
                .Replace("_CLUSTER_NAME_", name)
                .Replace("_CLUSTER_LOCATION_", this.settings.Region)
                .Replace("_USER_", this.settings.Username.ToUnsecureString())
                .Replace("_PWD_", this.settings.Password.ToUnsecureString());

            int ix = 1;
            foreach(int port in ports)
            {
                parameterContent = parameterContent.Replace($"_PORT{ix}_", port.ToString());
                ++ix;
            }

            await this.CreateTemplateDeploymentAsync(credential, name, templateContent, parameterContent);

            return (name + "." + this.settings.Region + ".cloudapp.azure.com");
        }

        public async Task DeleteClusterAsync(string name)
        {
            string rgName = name;

            string token = await this.GetAuthorizationTokenAsync();
            TokenCloudCredentials credential = new TokenCloudCredentials(this.settings.SubscriptionID.ToUnsecureString(), token);

            using (ResourceManagementClient resourceGroupClient = new ResourceManagementClient(credential))
            {
                AzureOperationResponse deleteResult = await resourceGroupClient.ResourceGroups.BeginDeletingAsync(rgName);
            }
        }

        public async Task<ClusterOperationStatus> GetClusterStatusAsync(string name)
        {
            string token = await this.GetAuthorizationTokenAsync();
            TokenCloudCredentials credential = new TokenCloudCredentials(this.settings.SubscriptionID.ToUnsecureString(), token);

            DeploymentGetResult dpResult;
            ResourceGroupGetResult rgResult;

            using (ResourceManagementClient templateDeploymentClient = new ResourceManagementClient(credential))
            {
                DeploymentExistsResult exists = templateDeploymentClient.Deployments.CheckExistence(name, name + "dp");
                if (!exists.Exists)
                {
                    // This might also imply that the cluster never existed in the first place.
                    return ClusterOperationStatus.ClusterNotFound;
                }


                dpResult = templateDeploymentClient.Deployments.Get(name, name + "dp");
                rgResult = templateDeploymentClient.ResourceGroups.Get(name);
            }

            //Either the resource group might exists, but resources are being added or deleted via the template, or the RG itself might be getting created or deleted.
            //This means we have to seaparate out the provisioning states of both the RG along with teh template deployment to get a cluster status. 
            //string result = dpResult.Deployment.Properties.ProvisioningState + rgResult.ResourceGroup.ProvisioningState;
            //result = result.Replace("Succeeded", "");
            if (rgResult.ResourceGroup.ProvisioningState.Contains("Failed"))
            {
                return ClusterOperationStatus.DeleteFailed;
            }

            if (rgResult.ResourceGroup.ProvisioningState.Contains("Deleting"))
            {
                return ClusterOperationStatus.Deleting;
            }

            if (dpResult.Deployment.Properties.ProvisioningState.Contains("Accepted") || dpResult.Deployment.Properties.ProvisioningState.Contains("Running"))
            {
                return ClusterOperationStatus.Creating;
            }

            if (dpResult.Deployment.Properties.ProvisioningState.Contains("Failed"))
            {
                return ClusterOperationStatus.CreateFailed;
            }

            if (dpResult.Deployment.Properties.ProvisioningState.Contains("Succeeded"))
            {
                return ClusterOperationStatus.Ready;
            }


            return ClusterOperationStatus.Unknown;
        }

        private async Task<string> GetAuthorizationTokenAsync()
        {
            ClientCredential cc = new ClientCredential(this.settings.ClientID.ToUnsecureString(), this.settings.ClientSecret.ToUnsecureString());

            AuthenticationContext context = new AuthenticationContext(this.settings.Authority.ToUnsecureString());
            AuthenticationResult result = await context.AcquireTokenAsync("https://management.azure.com/", cc);

            if (result == null)
            {
                throw new InvalidOperationException("Failed to obtain the JWT token");
            }

            return result.AccessToken;
        }

        private async Task<string> CreateResourceGroupAsync(TokenCloudCredentials credential, string rgName)
        {
            ResourceGroup resourceGroup = new ResourceGroup {Location = this.settings.Region};

            using (ResourceManagementClient resourceManagementClient = new ResourceManagementClient(credential))
            {
                ResourceGroupExistsResult exists = await resourceManagementClient.ResourceGroups.CheckExistenceAsync(rgName);

                if (exists.Exists)
                {
                    return "Exists";
                }

                ResourceGroupCreateOrUpdateResult rgResult = await resourceManagementClient.ResourceGroups.CreateOrUpdateAsync(rgName, resourceGroup);

                return rgResult.StatusCode.ToString();
            }
        }

        private async Task CreateTemplateDeploymentAsync(TokenCloudCredentials credential, string rgName, string templateContent, string parameterContent)
        {
            Deployment deployment = new Deployment();

            string deploymentname = rgName + "dp";
            deployment.Properties = new DeploymentProperties
            {
                Mode = DeploymentMode.Incremental,
                Template = templateContent,
                Parameters = parameterContent,
            };

            using (ResourceManagementClient templateDeploymentClient = new ResourceManagementClient(credential))
            {
                try
                {
                    DeploymentOperationsCreateResult dpResult =
                        await templateDeploymentClient.Deployments.CreateOrUpdateAsync(rgName, deploymentname, deployment);
                    ServiceEventSource.Current.Message("ArmClusterOperator: Deployment in RG {0}: {1} ({2})", rgName, dpResult.RequestId, dpResult.StatusCode);
                }
                catch (Exception e)
                {
                    ServiceEventSource.Current.Message(
                        "ArmClusterOperator: Failed deploying ARM template to create a cluster in RG {0}. {1}",
                        rgName,
                        e.Message);

                    throw;
                }
            }
        }

        private void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            this.UpdateClusterOperatorSettings(e.NewPackage.Settings);
        }

        private void CodePackageActivationContext_DataPackageModifiedEvent(object sender, PackageModifiedEventArgs<DataPackage> e)
        {
            this.UpdateArmTemplateContent(e.NewPackage.Path);
            this.UpdateArmParameterContent(e.NewPackage.Path);
        }

        private void UpdateClusterOperatorSettings(ConfigurationSettings settings)
        {
            KeyedCollection<string, ConfigurationProperty> clusterConfigParameters = settings.Sections["AzureSubscriptionSettings"].Parameters;

            // These settings are encrypted in Settings.xml using the PowerShell command:
            // Invoke-ServiceFabricEncryptText using a certificate 
            this.settings = new ArmClusterOperatorSettings(
                clusterConfigParameters["Region"].Value,
                clusterConfigParameters["ClientID"].DecryptValue(),
                clusterConfigParameters["ClientSecret"].DecryptValue(),
                clusterConfigParameters["Authority"].DecryptValue(),
                clusterConfigParameters["SubscriptionID"].DecryptValue(),
                clusterConfigParameters["Username"].DecryptValue(),
                clusterConfigParameters["Password"].DecryptValue());
        }

        private void UpdateArmTemplateContent(string templateDataPath)
        {
            using (StreamReader reader = new StreamReader(Path.Combine(templateDataPath, "PartyClusterTemplate.json")))
            {
                this.armTemplate = reader.ReadToEnd();
            }
        }

        private void UpdateArmParameterContent(string templateDataPath)
        {
            using (StreamReader reader = new StreamReader(Path.Combine(templateDataPath, "PartyClusterTemplate.Parameters.json")))
            {
                this.armParameters = reader.ReadToEnd();
            }
        }
    }
}