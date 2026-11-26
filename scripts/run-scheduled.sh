#!/bin/sh
set -e

CDPATH= cd "$(dirname "$0")/.."
if [ -f ./.env ]; then
    set -a
    . ./.env
    set +a
fi

exec dotnet src/PipelineAlerts/bin/Release/publish/PipelineAlerts.dll --scheduled .pipeline-alert-deliveries
