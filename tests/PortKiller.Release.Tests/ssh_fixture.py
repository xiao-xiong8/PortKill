"""Authenticated SSH/SFTP loopback fixture. No host services, global SSH config, or real accounts are changed."""
import asyncio
import json
import os
import logging
import pathlib
import shlex
import sys

sys.path.insert(0, sys.argv[1])
os.environ.setdefault("PATH", str(pathlib.Path(sys.executable).parent))
import asyncssh

settings = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8-sig"))
root = pathlib.Path(settings["root"]).resolve()
virtual = root / "virtual"
staging = virtual / "srv/portkiller-updates/.staging"
staging.mkdir(parents=True, exist_ok=True)
logging.basicConfig(filename=str(root / "ssh-debug.log"), level=logging.INFO)


class SubsystemServer(asyncssh.SFTPServer):
    def exit(self):
        # Match sshd's SFTP subsystem exit-status; Windows OpenSSH treats a missing status as failure.
        self.channel.exit(0)


def map_path(value):
    if value == "/srv/portkiller-updates/stable":
        return settings["public"]
    if value == "/etc/portkiller-updates/verification.json":
        return settings["verification"]
    prefix = "/srv/portkiller-updates/.staging/"
    if value.startswith(prefix):
        candidate = (staging / value[len(prefix):]).resolve()
        if not candidate.is_relative_to(staging):
            raise ValueError("Invalid staging path")
        return str(candidate)
    return value


async def execute(process):
    try:
        args = shlex.split(process.command)
        with (root / "commands.jsonl").open("a", encoding="utf-8") as output:
            output.write(json.dumps({"command": args}) + "\n")
        if len(args) == 5 and args[:4] == ["mkdir", "-p", "-m", "700"]:
            if not args[4].startswith("/srv/portkiller-updates/.staging/"):
                raise ValueError("Invalid mkdir path")
            pathlib.Path(map_path(args[4])).mkdir(parents=True, exist_ok=True)
            process.exit(0)
            return
        if not args or args[0] != "/opt/portkiller-updates/tool/PortKiller.UpdateTool" or args[1] not in {"inspect", "activate", "recover"}:
            raise ValueError("Unsupported test command")
        child = await asyncio.create_subprocess_exec(settings["dotnet"], settings["tool"],
            *[map_path(arg) for arg in args[1:]], stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        stdout, stderr = await asyncio.wait_for(child.communicate(), 50)
        process.stdout.write(stdout.decode("utf-8"))
        process.stderr.write(stderr.decode("utf-8"))
        process.exit(child.returncode)
    except Exception as error:
        process.stderr.write(type(error).__name__ + ": " + str(error) + "\n")
        process.exit(1)


async def main():
    host_key = asyncssh.generate_private_key("ssh-ed25519")
    client_key = asyncssh.generate_private_key("ssh-ed25519")
    client_path = root / "client-key"
    client_key.write_private_key(str(client_path))
    authorized = root / "authorized_keys"
    client_key.write_public_key(str(authorized))
    server = await asyncssh.create_server(asyncssh.SSHServer, "127.0.0.1", 0,
        config=[], server_host_keys=[host_key], authorized_client_keys=str(authorized), process_factory=execute,
        sftp_factory=lambda channel: SubsystemServer(channel, chroot=str(virtual).encode()), allow_scp=True)
    port = server.get_port()
    known = root / "known_hosts"
    known.write_text(f"[127.0.0.1]:{port} " + host_key.export_public_key().decode(), encoding="utf-8")
    ready = root / "ready.json"
    ready.write_text(json.dumps({"port": port}), encoding="utf-8")
    await asyncio.Event().wait()


asyncio.run(main())
