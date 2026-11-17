The workflow actions, template variables, debug info and validate endpoints no longer turn an unexpected error into a 400. It now surfaces as a 500 and is logged by the host.
