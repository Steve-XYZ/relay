# Relay sandbox: minimal toolchain for coding agents.
# Deliberately small; extend for real agents (node, python, etc.) as needed.
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        git \
        bash \
        coreutils \
        grep \
        sed \
        ripgrep \
    && rm -rf /var/lib/apt/lists/*

# Non-root compatibility: the worker passes --user $(id -u):$(id -g) and mounts
# the job worktree at /workspace plus helper scripts at /relay-assets.
WORKDIR /workspace

CMD ["sleep", "infinity"]
