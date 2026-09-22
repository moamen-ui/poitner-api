# Alerting runbook — observability baseline

Operator runbook for the two alert paths shipped by
[R5-58](../roadmap/execution/R5-58-observability-baseline.md). No Grafana/Loki/Datadog/Prometheus
stack exists — this is the minimum pre-launch alerting, not a full observability platform (see that
doc's §10 Out of scope).

## 1. Dead-man's-switch uptime alert (primary)

`UptimePingService` (`API/Hosted/UptimePingService.cs`) runs inside the api container. Every 60s it
GETs `http://localhost:8080/health`; only if that reports healthy does it GET the URL in
`UPTIME_PING_URL`. This mirrors the existing `OFFSITE_HEALTHCHECK_URL` pattern used by
`scripts/offsite-backup.sh`.

**Setup** (one-time, per environment):
1. Create a check at [healthchecks.io](https://healthchecks.io) (the same account already used for
   `OFFSITE_HEALTHCHECK_URL`) with a **2-minute grace period** (pings every 60s, so a missed ping
   is caught quickly without false-positiving on a single slow tick).
2. Set `UPTIME_PING_URL` in `.env.prod` to that check's ping URL.
3. Configure the check's notification channel (email, at minimum) in the healthchecks.io dashboard.

**What triggers the alert**: the api container is down, `/health` reports unhealthy (DB
unreachable), or the process is wedged — in every case, pings to `UPTIME_PING_URL` stop, and
healthchecks.io alerts the operator by e-mail after the grace period elapses.

**Silence is not health**: absence of an alert only means pings are still arriving. Confirm the
check is actually configured by visiting the healthchecks.io dashboard after deploy (see the
doc's §9 Release steps).

## 2. Caddy 5xx rate (secondary, log-based)

Caddy has no explicit `log` directive in the `Caddyfile`, so it logs to stdout in JSON by default
when run as a container; `docker compose logs caddy` shows these lines. There is no automated
alert wired for this today — use the one-liner below manually when investigating an incident, or
wire it into a cron + mail command if a standing check is wanted:

```bash
# Count 5xx responses in the last 5 minutes of Caddy access logs.
docker compose -f docker-compose.prod.yml logs --since 5m caddy \
  | grep -o '"status":[0-9]*' \
  | awk -F: '{print $2}' \
  | awk '$1 >= 500 {c++} END {print c+0}'
```

A result greater than 5 in a 5-minute window is the threshold worth investigating — check
`docker compose logs api` (JSON, request-id per line — see below) for the matching errors around
the same timestamps.

## 3. Correlating an incident with structured logs

Every API log line is one JSON object (built-in `JsonConsoleFormatter`, no Serilog) and carries a
`Scopes` entry with `RequestId` (always present), and `UserId`/`TenantId` (present only for
authenticated requests). `X-Request-Id` is echoed on every response — a customer or the dashboard
can report a request id from a `curl -sI` or the browser network tab, and:

```bash
docker compose -f docker-compose.prod.yml logs api | grep '<request-id>'
```

pulls every log line written while handling that one request, including the unhandled-exception
handler's log line if it errored (the request-id middleware is registered before that handler).

## 4. Out of scope

Grafana/Loki/Datadog/Prometheus metrics, distributed tracing, automated log shipping, an automated
Caddy 5xx alert, a dashboard health widget. See R5-58 §10 for the full list and rationale.
