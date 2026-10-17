#!/bin/sh
# Starts Apache mod_dav on http://localhost:8088/dav/ for the WebDAV conformance tests, then:
#   RECITAL_TEST_WEBDAV_URL=http://localhost:8088/dav/ RECITAL_TEST_WEBDAV_USER=recital \
#   RECITAL_TEST_WEBDAV_PASSWORD=recital-test-only dotnet test tests/Majorsilence.Recital.Storage.Tests
# Stop it with: docker rm -f recital-webdav
set -e
here=$(cd "$(dirname "$0")" && pwd)
docker rm -f recital-webdav >/dev/null 2>&1 || true
docker run -d --name recital-webdav -p 8088:80 \
  -v "$here/httpd.conf:/usr/local/apache2/conf/httpd.conf:ro" \
  -v "$here/webdav.passwd:/usr/local/apache2/conf/webdav.passwd:ro" \
  httpd:2.4.62 sh -c 'mkdir -p htdocs/dav var && chown daemon:daemon htdocs/dav var && exec httpd-foreground'
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' -u recital:recital-test-only -X PROPFIND -H 'Depth: 0' http://localhost:8088/dav/ || true)
  [ "$code" = "207" ] && exit 0
  sleep 1
done
docker logs recital-webdav
exit 1
