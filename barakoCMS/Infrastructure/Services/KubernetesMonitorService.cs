using k8s;
using k8s.Models;

namespace barakoCMS.Infrastructure.Services;

public interface IKubernetesMonitorService
{
    Task<ClusterStatus> GetClusterStatusAsync();
}

public class ClusterStatus
{
    public bool IsInCluster { get; set; }
    public bool IsConnected { get; set; }
    public string ConnectionMethod { get; set; } = "None";
    public List<NodeInfo> Nodes { get; set; } = new();
    public List<DeploymentInfo> Deployments { get; set; } = new();
    public string Error { get; set; } = string.Empty;
}

public class NodeInfo
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public class DeploymentInfo
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public int Replicas { get; set; }
    public int Available { get; set; }
}

public class KubernetesMonitorService : IKubernetesMonitorService
{
    private readonly ILogger<KubernetesMonitorService> _logger;
    private readonly Kubernetes? _client;
    private static bool _initFailed = false;
    private readonly IWebHostEnvironment? _env;
    private readonly IConfiguration _config;
    private readonly IConfigurationService _configService;

    public KubernetesMonitorService(ILogger<KubernetesMonitorService> logger, IServiceProvider serviceProvider, IConfiguration config, IConfigurationService configService)
    {
        _logger = logger;
        _env = serviceProvider.GetService<IWebHostEnvironment>();
        _config = config;
        _configService = configService;
        _client = CreateClient();
    }

    private Kubernetes? CreateClient()
    {
        if (_initFailed) return null;

        // User Request: Make it optional via config, default false
        var isEnabled = _config.GetValue<bool>("Kubernetes:Enabled");
        if (!isEnabled)
        {
            _logger.LogInformation("Kubernetes Monitoring is disabled via configuration.");
            return null;
        }

        try
        {
            if (KubernetesClientConfiguration.IsInCluster())
            {
                var config = KubernetesClientConfiguration.InClusterConfig();
                return new Kubernetes(config);
            }

            // Skip local config if already failed once or if in production/non-dev
            if (_env != null && _env.EnvironmentName != "Development")
            {
                return null;
            }

            // Allow fallback to local kubeconfig for dev
            var localConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            return new Kubernetes(localConfig);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Kubernetes Client. Monitoring will be disabled.");
            _initFailed = true;
            return null;
        }
    }

    public async Task<ClusterStatus> GetClusterStatusAsync()
    {
        var status = new ClusterStatus();

        // Check if Kubernetes monitoring is enabled via database setting
        var isEnabled = await _configService.GetConfigValueAsync("Kubernetes__Enabled", false);

        if (!isEnabled || _client == null)
        {
            status.IsConnected = false;
            status.IsInCluster = false;
            status.ConnectionMethod = "None";
            status.Error = isEnabled ? "Kubernetes monitoring is not initialized." : "Kubernetes monitoring is disabled via settings.";
            return status;
        }

        status.IsInCluster = KubernetesClientConfiguration.IsInCluster();
        status.ConnectionMethod = KubernetesClientConfiguration.IsInCluster() ? "InCluster" : "LocalConfig";

        try
        {
            // Fetch Nodes
            var nodes = await _client.CoreV1.ListNodeAsync();
            status.Nodes = nodes.Items.Select(n => new NodeInfo
            {
                Name = n.Metadata.Name,
                Status = n.Status.Conditions?.FirstOrDefault(c => c.Type == "Ready")?.Status == "True" ? "Ready" : "NotReady",
                Version = n.Status.NodeInfo.KubeletVersion,
                Role = n.Metadata.Labels.ContainsKey("kubernetes.io/role") ? n.Metadata.Labels["kubernetes.io/role"] : "worker"
            }).ToList();

            // Fetch Deployments (in current namespace or default)
            // We usually want to monitor barakocms deployments.
            // If InCluster, we can try to guess namespace or use "default".
            string ns = "default";
            // K8s client doesn't easily expose "current namespace" without file reading, defaulting to 'default' or 'barako-cms'
            // We will list all in 'default' for now as a POC.

            var deployments = await _client.AppsV1.ListNamespacedDeploymentAsync(ns);
            status.Deployments = deployments.Items.Select(d => new DeploymentInfo
            {
                Name = d.Metadata.Name,
                Namespace = d.Metadata.NamespaceProperty,
                Replicas = d.Status.Replicas ?? 0,
                Available = d.Status.AvailableReplicas ?? 0
            }).ToList();

            status.IsConnected = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Kubernetes status");
            status.IsConnected = false;
            status.Error = ex.Message;
        }

        return status;
    }
}
