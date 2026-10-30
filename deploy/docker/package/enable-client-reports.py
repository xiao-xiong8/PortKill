"""Add the two public POST routes to the verified Caddy deployment layout."""
import argparse
from pathlib import Path

REPORT_ROUTES = """    # PortKill client reports: keep all other management endpoints private.
    @client_reports {
        method POST
        path /api/v1/client-installations/register /api/v1/client-installations/report
    }
    handle @client_reports {
        reverse_proxy api:5081 {
            header_up Host {$ADMIN_DOMAIN}
        }
    }
"""


def configure_caddy(text):
    site = "{$UPDATES_DOMAIN} {"
    anchor = "    handle /stable/events {"
    if text.count(site) != 1:
        raise ValueError("Expected one UPDATES_DOMAIN site; review custom Caddy configuration")
    before, downloads = text.split(site, 1)
    if REPORT_ROUTES in downloads:
        if downloads.count(REPORT_ROUTES) != 1:
            raise ValueError("Duplicate client-report routes")
        return text
    if "client-installations" in downloads or "@client_reports" in downloads:
        raise ValueError("Existing custom client-report routes require review")
    if downloads.count(anchor) != 1:
        raise ValueError("Expected the stable/events handler; review custom Caddy configuration")
    return before + site + downloads.replace(anchor, REPORT_ROUTES + anchor, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.input.resolve() == args.output.resolve():
        parser.error("Write to a separate candidate file, validate, then reload Caddy")
    result = configure_caddy(args.input.read_text(encoding="utf-8-sig"))
    args.output.write_text(result, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
