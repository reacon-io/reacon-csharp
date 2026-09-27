set -eu
sh /suite/csharp-package.sh
cp -r /sdk/conformance/stream-csharp /results/stream-consumer
REACON_TEST_URL="$REACON_STREAM_TEST_URL" dotnet run --project /results/stream-consumer/Consumer.csproj -c Release --nologo
