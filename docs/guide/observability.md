# Observability & Monitoring

BarakoCMS v2.0 includes enterprise-grade observability features out of the box, allowing you to trace requests, debug production issues, and monitor system health without third-party tools.

---

## 🔍 Structured Logging (Serilog)

We utilize **Serilog** to provide structured, queryable logs.

### Configuration
Logging is configured via `appsettings.json` under the `Serilog` section. By default, we suppress noisy logs from Microsoft and System namespaces to focus on your application logic.

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft": "Warning",
      "System": "Warning",
      "FastEndpoints": "Warning"
    }
  }
}
```

### Log Output
*   **Development**: Logs are written to the **Console** with color-coding for readability.
*   **Production**: Logs are written to **rolling JSON files** in the `/logs` directory (e.g., `logs/barako-log-20251216.json`). This makes them easy to ingest into tools like Datadog, ELK Stack, or Seq.

---

## 🔗 Correlation IDs

Every HTTP request is assigned a unique **Correlation ID**. This ID allows you to track a single user request across multiple logs, even if it triggers multiple internal actions.

*   **Header**: `X-Correlation-ID`
*   **Behavior**:
    *   If the client sends this header, the server uses it.
    *   If missing, the server generates a new UUID.
    *   The ID is returned in the response headers.
    *   The ID is attached to **every log entry** created during that request.

**Debugging Example**:
1.  User reports an error and gives you their Correlation ID: `abc-123-xyz`.
2.  You search your log files: `grep "abc-123-xyz" logs/*.json`.
3.  You see the entire story of that request, from start to failure.

---

## 🚦 Request Logging

The **Request/Response Middleware** automatically logs the outcome of every HTTP request, including:
*   HTTP Method & Path
*   Response Status Code
*   Duration (Latency)

**Example Log**:
```text
[WRN] HTTP GET /api/contents responded 405 in 4.4409ms
```

---

## ❤️ Health Monitoring

BarakoCMS exposes standard health check endpoints for container orchestrators (Kubernetes) and load balancers.

### Endpoints
*   `/health`: Returns a JSON status object.
    ```json
    { "status": "Healthy", "totalDuration": "0.002s" }
    ```
*   `/health-ui`: A visual dashboard showing the status of the API, Database, other dependencies.

### Configuration
The Health UI is available at `http://localhost:5005/health-ui`. It polls the internal `/health` endpoint every 10 seconds (configurable).
