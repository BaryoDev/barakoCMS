# Observability

`grafana-dashboard.json` is a Grafana dashboard, not a Kubernetes manifest. It lived in `k8s/`,
where `kubectl apply -f k8s/` picked it up and failed on it with "apiVersion not set, kind not set"
before reaching anything else. Import it through Grafana, not kubectl.
