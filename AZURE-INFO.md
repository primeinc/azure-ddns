# Azure Infrastructure Information

## Azure Subscriptions

| Subscription Name | Subscription ID | Status | DNS Zones |
|-------------------|-----------------|--------|-----------|
| Azure subscription 1 | `75f63a79-b221-4f1b-aefe-6e8602c40b48` | Enabled | 3 |
| 4PP Production Core | `3e4d24be-946c-4d3e-be66-700539b4da74` | Enabled | 0 |
| 4PP Development Core | `e4eb7658-b887-4ac5-b23a-e9499031d59d` | Enabled | 0 |
| 4PP Sandbox Lab | `98a49ff3-2204-48c2-9908-6d6c121ee372` | Enabled | 0 |

**User:** `will@4pp.dev`  
**Tenant:** Title Solutions Agency, LLC (`titlesolutionsllc.com`)

## DNS Zones

### Azure subscription 1
**Resource Group:** `domains-dns`

- **title.dev** - 22 record sets
- **title.solutions**
- **titlesolutionsllc.com**

#### title.dev Name Servers
- ns1-03.azure-dns.com
- ns2-03.azure-dns.net
- ns3-03.azure-dns.org
- ns4-03.azure-dns.info

### Other Subscriptions
- **4PP Production Core:** No DNS zones
- **4PP Development Core:** No DNS zones  
- **4PP Sandbox Lab:** No DNS zones

## Microsoft.Network Provider Status

| Subscription | Registration Status |
|--------------|-------------------|
| Azure subscription 1 | Registered |
| 4PP Production Core | Registered (2025-01-03) |
| 4PP Development Core | Registered |
| 4PP Sandbox Lab | Registered (2025-01-03) |

**Note:** Production Core and Sandbox Lab required registration of the Microsoft.Network provider on 2025-01-03.

---
*Last Updated: 2025-01-03*