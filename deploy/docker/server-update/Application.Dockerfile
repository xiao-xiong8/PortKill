FROM docker:28.5.2-cli AS dockercli
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libicu72 libssl3 zlib1g libstdc++6 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 portkill \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --shell /usr/sbin/nologin portkill
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=dockercli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/lib/docker/cli-plugins/docker-compose
WORKDIR /opt/portkill
COPY server-bundle/api/ api/
COPY server-bundle/worker/ worker/
COPY server-bundle/notifier/ notifier/
COPY server-bundle/tool/ tool/
# The SSH package is extracted with root-only permissions. COPY preserves them.
# Keep code root-owned while allowing the service account to load libraries.
RUN find /opt/portkill -type d -exec chmod 0755 {} + \
    && find /opt/portkill -type f -exec chmod 0644 {} + \
    && chmod 0755 api/PortKiller.UpdateAdmin.Api worker/PortKiller.UpdatePublisher.Worker notifier/PortKiller.UpdateNotifier tool/PortKiller.UpdateTool
USER 10001:10001
