# Scanning uploads for malware

A CMS that accepts files and serves them back is a distribution channel. That is fine while the only
uploader is a trusted editor, and it stops being fine the moment a form on a client site accepts a
CV, a receipt photo, or anything a stranger sends.

Scanning is off unless you configure it. Turning it on is two settings and a container.

## Turning it on

```json
{
  "Files": {
    "Scanner": {
      "Address": "clamav:3310",
      "TimeoutSeconds": 30
    }
  }
}
```

Or as environment variables, which is how the compose files pass everything else:

```
Files__Scanner__Address=clamav:3310
Files__Scanner__TimeoutSeconds=30
```

Leave `Address` empty or unset and nothing is scanned, which is what every deployment before this
did. There is no third state: the address is either there or it is not.

## The container

ClamAV runs as a daemon and barakoCMS talks to it over TCP. The official image works as it comes:

```yaml
  clamav:
    image: clamav/clamav:stable
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "clamdcheck.sh"]
      interval: 30s
      timeout: 10s
      retries: 5
      start_period: 5m
```

`start_period` is long on purpose. A fresh container downloads the signature database before clamd
answers anything, which takes a few minutes on a first run, and a shorter period reports the
container as unhealthy while it is doing exactly what it should.

The daemon needs about a gigabyte of memory to hold the signatures. That is the real cost of turning
this on, and it is worth knowing before you size a box for it.

## What happens to a file

The scan runs **before** the file is stored. A file that is stored first and scanned second is
downloadable for the length of the scan, and if the process dies in between it stays downloadable
forever with nothing recording that it was never checked.

There are three outcomes.

**Clean.** The upload proceeds exactly as it does with no scanner.

**Infected.** `422`, and the response names what the scanner found, because "rejected" with no reason
is indistinguishable from a broken upload button.

**Not scanned.** `503`. The scanner was unreachable, timed out, or answered something this does not
recognise. The upload is refused.

That last one is the decision worth reading twice: **an outage refuses uploads**. The alternative is
to store the file and hope, and the day the scanner is unreachable is exactly the day something may
be trying to make it so. If refusing uploads during a clamd outage is worse for you than accepting
unscanned ones, the honest configuration is to turn scanning off rather than to make it advisory.

## What is kept

Nothing of the file. It is refused before storage, so there is no copy on the deployment to serve by
accident or to forget to clean up.

What is kept is the record, in the audit log: the file name, its type, its size, who sent it, their
IP, and what the scanner called it. The actions are `file.refused.infected` and
`file.refused.unscanned`. The audit log is hash chained, so a refusal cannot be quietly removed from
it either.

This is deliberately not a quarantine in the usual sense of keeping the file somewhere an operator
can retrieve it. An operator needs to know what was refused and why, and that is what this gives
them. Keeping the bytes would mean holding malware on the deployment to answer a question the record
already answers.

## Checking it works

The EICAR test file is the standard way, and it is not malware: it is a 68-byte string every scanner
recognises by agreement so that exactly this can be tested.

```bash
printf 'X5O!P%%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*' > eicar.png
curl -F file=@eicar.png -H "Authorization: Bearer $TOKEN" https://your-instance/api/files
```

A working setup answers 422 naming `Eicar-Signature` or similar, and the audit log gains a
`file.refused.infected` entry. A 201 means the scan is not running, whatever the configuration says.

## What is not covered

Files already stored. Scanning those is a separate job with different operational risk: it runs
against live data, it can find something in a file people are already using, and deciding what
happens then is not a decision to make inside an upload handler.
