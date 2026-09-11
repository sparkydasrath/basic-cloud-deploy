using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Constructs;

namespace BasicappInfra
{
    public class BasicappInfraStack : Stack
    {
        internal BasicappInfraStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // The code that defines your stack goes here
            
            // A VPC spread across 2 Availability Zones. CDK creates public + private
            // subnets, an internet gateway, and (by default) a NAT gateway automatically.
            Vpc vpc = new(this, "BasicAppVpc", new VpcProps
            {
                MaxAzs = 2, // Default is all AZs in the region
                NatGateways = 1, // Default is 1 NAT Gateway per AZ
            });
            
            // ECS cluster that will run the Fargate service
            Cluster cluster = new(this, "BasicAppCluster", new ClusterProps
            {
                Vpc = vpc,
                ClusterName = "BasicAppCluster"
            });
            
            // Create an ECR repository for the Docker image
            IRepository repository = Repository.FromRepositoryName(this, "BasicAppRepository", "dotnet/basicdeploy");
            
            // Create a Fargate service and make it public
            ApplicationLoadBalancedFargateService service = new(this, "BasicAppFargateService", new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                DesiredCount = 2, // 2 tasks for high availability
                Cpu = 512, // 0.5 vCPU
                MemoryLimitMiB = 1024, // 1 GB
                CircuitBreaker = new DeploymentCircuitBreaker
                {
                    Rollback = true // Automatically rollback if the deployment fails
                },
                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    // pull the image you pushed to ECR. Use a real tag in CI; 'a51d6e6'
                    // is fine while you are learning this one construct.
                    Image = ContainerImage.FromEcrRepository(repository, "a51d6e6"),
                    ContainerPort = 8080,
                    Environment = new Dictionary<string, string>
                    {
                        { "ASPNETCORE_ENVIRONMENT", "Production" }
                    }
                },
                PublicLoadBalancer = true // the ALB gets a public DNS name
            });
            
            service.TargetGroup.ConfigureHealthCheck(new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
            {
                Path = "/health",
                HealthyHttpCodes = "200",
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 5
            });
            
            new CfnOutput(this, "ApiUrl", new CfnOutputProps
            {
                Value = $"http://{service.LoadBalancer.LoadBalancerDnsName}",
                Description = "Public URL of the API/The DNS name of the load balancer",
                ExportName = "LoadBalancerDNS"
            });
            
        }
    }
}
