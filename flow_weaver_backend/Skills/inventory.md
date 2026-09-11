# Skill: inventory — devices, pools, sources, credentials

The inventory domain is the set of **things workflows run against**.

## API — `fw_inventory`

Covers four resource families. Use `discover_operations(api="fw_inventory")`
to enumerate; here are the common entry points:

| Family | Purpose |
|---|---|
| devices | individual routers, switches, hosts, VMs |
| device_pools | named groups of devices (static members or dynamic queries) |
| inventory_sources | connectors that pull devices from NetBox / CSV / etc. |
| credentials | named auth material (metadata only — never the plaintext) |

### Quick lookups via dedicated tool

For ad-hoc searches ("find all junos devices in site Madrid") use the
dedicated tool `query_devices(platform, vendor, site, role, limit)` — it's
faster than `list_devices` for small result sets and returns a compact
projection.

## Credentials — critical rules

- Credentials hold **metadata only** through the API: username, type
  (ssh / rest / snmp), optional fields. The **password / private key /
  token lives encrypted** and is never returned.
- To rotate: the user edits in `/credentials` UI (not chat). The agent
  can `update_credential` with a new `username` or `type`, but not values.
- Specs that call external APIs use `${secret:credential:<id>:password}`
  to inject at call time — you don't need (or have) the plaintext.

If a workflow fails because a credential is wrong, tell the user: "the
credential `<name>` looks stale; rotate it in `/credentials` and rerun."
Don't try to "fix" it from the chat.

## Device pools — static vs dynamic

A `device_pool` can hold:

- `static_members`: explicit list of device ids.
- `filter_query`: a JMESPath-like filter evaluated over all devices in
  inventory ("every device where platform = junos and site = NYC").

When the user asks "add these 3 devices to pool X":

1. `get_device_pool` to read current members.
2. `update_device_pool` with the merged list.
3. Show the before/after count in the confirmation plan.

## Inventory sources

An `inventory_source` pulls devices from an external CMDB on a schedule.
Touching this rarely needs the chat — usually admins wire it once.
Available today: list / get / create / update / delete. **There is no
"force sync" endpoint** in the current API; if the user wants an
immediate refresh, point them at the admin UI or the backend team — the
agent can't trigger it.

## What you CAN'T do

- Reveal a credential value.
- Delete a device that's referenced by a live workflow run (the backend
  will 409 `device_in_use`).
- Create devices in production sites without a confirmation from the user.
