- **dev-playground deploys through `baryovm stack update` instead of a forced-command script on the
  VM.** The deploy logic now lives in the tool used to run that VM by hand, so a change to how a
  stack is deployed is made in one place rather than in a script on the box that nothing tests. It
  is also a real improvement on the old path: `stack update` recreates only the services whose
  images actually changed, waits for the health URL, and restores the previous images if the stack
  does not come back, where the script recreated everything and then reported whether a health check
  passed. The VM's host key is pinned from `.github/baryovm/known_hosts` and
  `BARYOVM_STRICT_HOST_KEYS=1` refuses an unknown host, because a runner has no memory between runs
  and learning a key every run is not verification. **The trade, stated because it is real: this
  job's key is not restricted to a forced command the way the playground and admin deploy keys
  still are.** BaryoVM runs arbitrary remote commands by design, so `DEV_DEPLOY_KEY` can reach
  anything the `opc` user can on every stack that VM hosts. It is a separate key and can be revoked
  on its own.
