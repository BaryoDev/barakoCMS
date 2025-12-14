#!/bin/sh

# Generate env-config.js from environment variables
# We use a relative path to 'public/env-config.js' so it works in different environments
echo "window._env_ = {" > ./public/env-config.js

# Only include variables starting with NEXT_PUBLIC_
printenv | grep NEXT_PUBLIC_ | while read -r line; do
  key=$(echo $line | cut -d '=' -f 1)
  value=$(echo $line | cut -d '=' -f 2-)
  echo "  $key: \"$value\"," >> ./public/env-config.js
done

echo "};" >> ./public/env-config.js

# Start the application
exec "$@"
