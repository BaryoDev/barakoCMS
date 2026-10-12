- **`Files:S3:UsePublicReadAcl` defaulted to `true`, which a new AWS bucket refuses.** It now
  defaults to `false`. A new AWS bucket has Object Ownership set to bucket owner enforced and
  refuses ACLs, and R2, SeaweedFS and Garage ignore or refuse them, so for those nothing changes.
  **Upgrade step:** a deployment on an AWS bucket with ACLs enabled that never set the option and
  relies on the ACL for public files stops getting public-read objects: set
  `Files__S3__UsePublicReadAcl=true`, or grant read on `public/*` (see the module README). Files
  already uploaded keep their ACL. `BarakoCMS.Files.S3` is 5.0.0.
