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
    private readonly KubernetesClientProvider _clients;
    private readonly IWebHostEnvironment? _env;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public KubernetesMonitorService(ILogger<KubernetesMonitorService> logger, IServiceProvider serviceProvider, IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _env = serviceProvider.GetService<IWebHostEnvironment>();
        _config = config;
        _scopeFactory = scopeFactory;
        _clients = new KubernetesClientProvider(BuildClient, logger);
    }

    /// <summary>
    /// Returns null when there is nothing to connect to, and throws when a connection was
    /// configured and could not be built. KubernetesClientProvider retries those two differently.
    /// </summary>
    private Kubernetes? BuildClient()
    {
        if (KubernetesClientConfiguration.IsInCluster())
        {
            var config = KubernetesClientConfiguration.InClusterConfig();
            _logger.LogInformation("Kubernetes client initialized using InCluster configuration.");
            return new Kubernetes(config);
        }

        try
        {
            var localConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            _logger.LogInformation("Kubernetes client initialized using local kubeconfig.");
            return new Kubernetes(localConfig);
        }
        catch (Exception localEx)
        {
            _logger.LogInformation("Local kubeconfig not available: {Error}. This is normal if Kubernetes is not installed.", localEx.Message);
            return null;
        }
    }

    public async Task<ClusterStatus> GetClusterStatusAsync()
    {
        var status = new ClusterStatus();

        // Check if Kubernetes monitoring is enabled via database setting
        // Create a scope to resolve the scoped IConfigurationService
        bool isEnabled = false;
        using (var scope = _scopeFactory.CreateScope())
        {
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            isEnabled = await configService.GetConfigValueAsync("Kubernetes__Enabled", false);
        }

        // Resolved per call, not once in the constructor: a client that could not be built at pod
        // start is rebuilt on a later call rather than leaving monitoring off until a restart.
        var client = isEnabled ? _clients.GetClient() : null;

        _logger.LogInformation("Kubernetes monitoring enabled check: {IsEnabled}, Client initialized: {ClientInitialized}",
            isEnabled, client != null);

        if (!isEnabled || client == null)
        {
            status.IsConnected = false;
            status.IsInCluster = false;
            status.ConnectionMethod = "None";

            // Provide clearer error messages
            if (!isEnabled)
            {
                status.Error = "Kubernetes monitoring is disabled via settings.";
                _logger.LogInformation("Kubernetes monitoring is disabled via settings");
            }
            else
            {
                status.Error = "Kubernetes monitoring is not available in this environment. No cluster connection could be established.";
                _logger.LogWarning("Kubernetes client is null - no cluster connection available. Attempts so far: {Attempts}", _clients.Attempts);
            }

            return status;
        }

        status.IsInCluster = KubernetesClientConfiguration.IsInCluster();
        status.ConnectionMethod = KubernetesClientConfiguration.IsInCluster() ? "InCluster" : "LocalConfig";
        _logger.LogInformation("Attempting to fetch Kubernetes status. IsInCluster: {IsInCluster}, Method: {Method}",
            status.IsInCluster, status.ConnectionMethod);

        try
        {
            // Fetch Nodes
            _logger.LogDebug("Fetching Kubernetes nodes...");
            var nodes = await client.CoreV1.ListNodeAsync();
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

            _logger.LogDebug("Fetching Kubernetes deployments in namespace: {Namespace}", ns);
            var deployments = await client.AppsV1.ListNamespacedDeploymentAsync(ns);
            status.Deployments = deployments.Items.Select(d => new DeploymentInfo
            {
                Name = d.Metadata.Name,
                Namespace = d.Metadata.NamespaceProperty,
                Replicas = d.Status.Replicas ?? 0,
                Available = d.Status.AvailableReplicas ?? 0
            }).ToList();

            status.IsConnected = true;
            _logger.LogInformation("Successfully fetched Kubernetes status: {NodeCount} nodes, {DeploymentCount} deployments",
                status.Nodes.Count, status.Deployments.Count);
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
