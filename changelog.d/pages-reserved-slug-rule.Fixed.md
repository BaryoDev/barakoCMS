- **A top-level page cannot take a reserved slug whatever its slug field's sensitivity**
  (BarakoCMS.Pages 4.3.1). The check on write reads the slug the way core's authoring checks do,
  so a slug field marked Sensitive is still checked.
