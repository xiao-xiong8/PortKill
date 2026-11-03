FROM docker:28.5.2-cli AS dockercli
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libicu72 libssl3 zlib1g libstdc++6 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=dockercli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/lib/docker/cli-plugins/docker-compose
WORKDIR /opt/portkill
COPY server-bundle/runner/ runner/
RUN chmod +x runner/PortKiller.ServerUpdateRunner
ENTRYPOINT ["/opt/portkill/runner/PortKiller.ServerUpdateRunner"]
