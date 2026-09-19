- **A holdout run with every hunk declared untested reported that all bindings had been held out
  and had failed as required, having held out nothing.** Found by pointing it at a pull request that
  changes a shipped blueprint and a changelog. The exit code was right, since declaring every hunk
  untested is a legitimate answer for a change that ships data rather than behaviour, but the
  sentence borrowed the words of a run that proved something. Pasted onto a pull request it would
  read as evidence of a check that never ran, which is the failure holdout exists to catch. It now
  names what happened and lists the untested hunks, and it stops before building a tree it has no
  binding to test. The success line counts the bindings it actually held out.
