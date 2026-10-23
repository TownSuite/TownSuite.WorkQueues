#!/bin/bash
set -e # exit on first error
set -u # exit on using unset variable

CURRENTPATH=$(pwd)

builderName="townsuite-builder"
if docker buildx inspect "$builderName" >/dev/null 2>&1; then
    docker buildx use "$builderName"
else
    docker buildx create --name "$builderName" --driver docker-container --use >/dev/null
fi
docker buildx inspect "$builderName" --bootstrap
mkdir -p "$CURRENTPATH/build/oci"

echo "Building townsuite/workqueues-server"
docker buildx build -f ./TownSuite.WorkQueues.Server/Dockerfile --progress plain --provenance=true --sbom=true --output "type=oci,name=townsuite/workqueues-server,oci-mediatypes=true,compression=zstd,force-compression=true,tar=false,dest=$CURRENTPATH/build/oci/workqueues-server" .
tar -cvf "$CURRENTPATH/build/oci/workqueues-server.oci.tar" -C "$CURRENTPATH/build/oci/workqueues-server" --transform='s|^\./||' .
rm -rf "$CURRENTPATH/build/oci/workqueues-server"
