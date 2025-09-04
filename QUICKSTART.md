# Azure DDNS Quick Start Guide

Your Azure DDNS service is deployed and running at:
**https://func-api-cq3maraez745s.azurewebsites.net**

## Available Endpoints

### 1. Claim a Hostname (Browser)
Navigate to: `https://func-api-cq3maraez745s.azurewebsites.net/api/manage/{hostname}`
- Example: `https://func-api-cq3maraez745s.azurewebsites.net/api/manage/mydevice`
- You'll be redirected to Azure AD login
- After authentication, the hostname is claimed and you'll receive an API key

### 2. Update DNS Record (Router/Script)
```bash
# DynDNS2 Protocol (works with most routers)
curl -u "hostname:apikey" "https://func-api-cq3maraez745s.azurewebsites.net/api/nic/update?hostname=mydevice.title.dev&myip=auto"

# Or specify IP explicitly
curl -u "hostname:apikey" "https://func-api-cq3maraez745s.azurewebsites.net/api/nic/update?hostname=mydevice.title.dev&myip=203.0.113.42"
```

Expected responses:
- `good` - IP updated successfully
- `nochg` - IP unchanged
- `nohost` - Hostname not found or not owned by API key
- `badauth` - Invalid or missing API key

### 3. API Key Management

#### Generate New API Key
```bash
POST https://func-api-cq3maraez745s.azurewebsites.net/api/GenerateNewApiKey
Authorization: Bearer {azure-ad-token}
Content-Type: application/json

{
  "hostname": "mydevice"
}
```

#### Revoke API Key
```bash
POST https://func-api-cq3maraez745s.azurewebsites.net/api/RevokeApiKey
Authorization: Bearer {azure-ad-token}
Content-Type: application/json

{
  "hostname": "mydevice",
  "apiKey": "key-to-revoke"
}
```

### 4. Health Check
```bash
curl https://func-api-cq3maraez745s.azurewebsites.net/api/health
```

## Router Configuration

Most routers support DynDNS2 protocol. Use these settings:

- **Service**: Custom/DynDNS2
- **Server**: `func-api-cq3maraez745s.azurewebsites.net`
- **Path**: `/api/nic/update`
- **Username**: Your hostname (e.g., `mydevice`)
- **Password**: Your API key
- **Hostname**: Full DNS name (e.g., `mydevice.title.dev`)

## ddclient Configuration

For Linux/Unix systems using ddclient:

```conf
# /etc/ddclient.conf
protocol=dyndns2
use=web
server=func-api-cq3maraez745s.azurewebsites.net
script=/api/nic/update
login=mydevice
password='your-api-key-here'
mydevice.title.dev
```

## Testing

Test your configuration:
```bash
# Check current DNS record
nslookup mydevice.title.dev

# Update with new IP
curl -u "mydevice:your-api-key" \
  "https://func-api-cq3maraez745s.azurewebsites.net/api/nic/update?hostname=mydevice.title.dev&myip=1.2.3.4"

# Verify update
nslookup mydevice.title.dev
```

## Cost Monitoring

Monitor your costs at:
- **Resource Group Budget**: Azure Portal > rg-azure-ddns-dev > Cost Management > Budgets
- **Current Month Costs**: Budget alerts at 50%, 75%, 90%, 100% of $50 limit
- **Email Alerts**: Sent to will@4pp.dev, shelby@4pp.dev

## Troubleshooting

1. **401 Unauthorized**: Check your API key is correct
2. **nohost**: Hostname not claimed or doesn't match API key
3. **DNS not updating**: Verify managed identity has DNS Zone Contributor role
4. **Function app down**: Check health endpoint first

## Support

Report issues at: https://github.com/your-org/azure-ddns/issues