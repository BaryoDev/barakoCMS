- **The release refuses to run if a test project is not covered.** `release.yml` names one test project
  by path. That is correct while there is one and a silent hole the moment somebody adds a second: the
  new suite would sit in the repo, never run, and the packages would publish anyway. The workflow now
  enumerates `*.Tests.csproj` and fails if what it finds is not what it runs. The suite-actually-ran
  floor moves from 500 to 900.
