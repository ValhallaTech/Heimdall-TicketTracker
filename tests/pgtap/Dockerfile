# Dockerfile for local pgTAP testing.
#
# Extends the official postgres image with the postgresql-17-pgtap
# extension package. Used by docker-compose.pgtap.yml. CI does not
# build this image — see .github/workflows/ci.yml for the equivalent
# install-into-service-container approach.

FROM postgres:17

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-17-pgtap \
    && rm -rf /var/lib/apt/lists/*
