Webhooks:AllowedHosts entries now take an optional port. An entry without a port allows only 80 and 443, so a listed host is no longer open on every port. A value that is not an array logs a warning.
