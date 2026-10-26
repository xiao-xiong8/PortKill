#!/usr/bin/env python3
"""Resolve a container's actual image across legacy and containerd image stores."""
import json
import shutil
import subprocess
import sys


def docker(*arguments):
    result = subprocess.run([shutil.which("docker") or "docker", *arguments], capture_output=True, text=True, timeout=30)
    return result.stdout if result.returncode == 0 else None


def inspect_image(reference, platform=None):
    arguments = ["image", "inspect"]
    if platform:
        arguments += ["--platform", platform]
    output = docker(*arguments, reference)
    if output is None:
        return None
    images = json.loads(output)
    return images[0] if len(images) == 1 else None


def image_identities(image):
    values = {image.get("Id"), (image.get("Descriptor") or {}).get("digest")}
    for manifest in image.get("Manifests") or []:
        values.add((manifest.get("Descriptor") or {}).get("digest"))
    return values - {None, ""}


def is_verified_snapshot(reference, image, container):
    config = image.get("Config") or {}
    labels = config.get("Labels") or {}
    if labels.get("io.portkill.recovery.container") != container.get("Id") or not container.get("Id"):
        return False
    if config.get("User") != "10001:10001" or config.get("WorkingDir") != "/opt/portkill" or config.get("Entrypoint"):
        raise RuntimeError("旧容器恢复镜像的运行配置不匹配，停止更新。")
    check = 'test "$(id -u)" = 10001 && test "$PWD" = /opt/portkill && /opt/portkill/notifier/PortKiller.UpdateNotifier; result=$?; test "$result" -eq 2'
    if docker("run", "--rm", "--network", "none", "--entrypoint", "/bin/sh", reference, "-c", check) is None:
        raise RuntimeError("旧容器恢复镜像的通知程序自检失败，停止更新。")
    print("旧容器恢复镜像自检通过，将用它保留回滚能力。", file=sys.stderr)
    return True


def resolve(container_id, component="notifier"):
    output = docker("inspect", container_id)
    if output is None:
        raise RuntimeError(f"无法读取原 {component} 容器，停止更新。")
    containers = json.loads(output)
    if len(containers) != 1:
        raise ValueError(f"需要原部署中的单个 {component} 容器。")
    container = containers[0]
    descriptor = container.get("ImageManifestDescriptor") or {}
    identities = {value for value in (container.get("Image"), descriptor.get("digest")) if value}
    if not identities:
        raise ValueError(f"原 {component} 容器没有可核对的镜像身份。")

    # A container Image value need not be a taggable image-store ID on containerd.
    for identity in sorted(identities):
        image = inspect_image(identity)
        if image is not None:
            return identity

    platform_info = descriptor.get("platform") or {}
    platform = "/".join(filter(None, (platform_info.get("os", "linux"), platform_info.get("architecture", "amd64"), platform_info.get("variant"))))
    listed = docker("image", "ls", "-a", "-q", "--no-trunc")
    if listed is None:
        raise RuntimeError("无法列出本地镜像，停止更新。")
    # Config.Image may now name a newer build. It is only a search hint, never proof.
    candidates = [container.get("Config", {}).get("Image"), "portkill-notifier:update-base", "portkill-notifier:latest", *listed.splitlines()]
    seen = set()
    for candidate in candidates:
        if not candidate or candidate in seen:
            continue
        seen.add(candidate)
        image = inspect_image(candidate)
        if image is None:
            continue
        if image_identities(image) & identities:
            return candidate
        if component == "notifier" and is_verified_snapshot(candidate, image, container):
            return candidate
        platform_image = inspect_image(candidate, platform)
        if platform_image is not None and image_identities(platform_image) & identities:
            # Keep the reference that the daemon actually resolved. On some Docker
            # versions the returned Id is a config digest, not a taggable reference.
            return candidate

    raise RuntimeError(f"无法找到与原 {component} 容器匹配的本地镜像；未构建或切换容器。请保留当前容器和镜像并提供诊断结果。")


if __name__ == "__main__":
    try:
        if len(sys.argv) not in (2, 3) or len(sys.argv) == 3 and sys.argv[2] not in ("worker", "notifier"):
            raise ValueError("用法：resolve-notifier-image.py <container-id> [worker|notifier]")
        print(resolve(sys.argv[1], sys.argv[2] if len(sys.argv) == 3 else "notifier"))
    except (ValueError, KeyError, TypeError, RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
