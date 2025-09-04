# Azure Dynamic DNS Service

> **⚠️ PROOF OF CONCEPT - NOT PRODUCTION READY**
> 
> This is an experimental implementation that demonstrates DDNS functionality with Azure Functions. 
> It requires additional security hardening, testing, and operational features before production use.
> Use at your own risk and expect breaking changes.

A modern Dynamic DNS (DDNS) service built on Azure Functions that provides DynDNS2 protocol compatibility for updating DNS records in Azure DNS zones. Features API key authentication, web-based management, and compatibility with UniFi Dream Machines and other routers.

## 🚀 Features

- **DynDNS2 Protocol Support**: Compatible with UniFi, Inadyn, ddclient, and other standard DDNS clients
- **API Key Authentication**: Secure per-hostname API keys instead of shared credentials
- **Web Management Interface**: Claim hostnames and manage API keys through a browser
- **Azure DNS Integration**: Direct updates to Azure-hosted DNS zones
- **Admin Panel**: Monitor all hostnames, users, and update statistics
- **Cross-Subscription Support**: DNS zones can be in different subscriptions
- **Modern Architecture**: .NET 8 isolated process on Flex Consumption plan

## 🏗️ Architecture

Built with:
- **.NET 8**: Isolated process Azure Functions
- **Azure DNS**: Native DNS zone management
- **Azure Table Storage**: Hostname ownership and API key storage
- **Azure AD/Entra ID**: User authentication for web interface
- **Application Insights**: Monitoring and telemetry
- **Bicep/Azure Developer CLI**: Infrastructure as Code

## 📦 Quick Deploy

### Prerequisites

- Azure subscription
- Azure DNS zone already configured
- Azure Developer CLI (`azd`) installed
- .NET 8 SDK installed

### Deploy with Azure Developer CLI

```bash
# Clone the repository
git clone https://github.com/primeinc/azure-ddns
cd azure-ddns

# Deploy to Azure
azd up

# Follow the prompts to configure:
# - DNS subscription ID
# - DNS resource group
# - DNS zone name
# - Azure AD configuration
```

## 🔧 Configuration

### Environment Variables

The function app requires these settings:

| Setting | Description | Example |
|---------|-------------|---------|
| `DNS_SUBSCRIPTION_ID` | Subscription containing DNS zone | `uuid` |
| `DNS_RESOURCE_GROUP` | Resource group with DNS zone | `rg-dns` |
| `DNS_ZONE_NAME` | Azure DNS zone name | `example.com` |
| `DDNS_SUBDOMAIN` | Subdomain for DDNS records | `ddns-sandbox` |
| `AZURE_AD_TENANT_ID` | Azure AD tenant for auth | `uuid` |
| `AZURE_AD_CLIENT_ID` | Azure AD app registration | `uuid` |

## 📱 Usage

### Web Interface (Claim & Manage Hostnames)

1. Navigate to `https://your-function.azurewebsites.net/api/manage/device.ddns-sandbox.example.com`
2. Sign in with Azure AD
3. Claim the hostname if unclaimed
4. Generate API keys for your devices
5. View update history and statistics

### Router Configuration

#### UniFi Dream Machine

1. Settings → Internet → WAN → Dynamic DNS
2. Create new service:
   - **Service**: `custom`
   - **Hostname**: `device.ddns-sandbox.example.com`
   - **Username**: `device` (hostname prefix)
   - **Password**: Your API key
   - **Server**: `your-function.azurewebsites.net/api/nic/update?hostname=%h&myip=%i`

#### Inadyn

```conf
custom your-function.azurewebsites.net {
    hostname    = "device.ddns-sandbox.example.com"
    username    = "device"
    password    = "your-api-key"
    ddns-server = "your-function.azurewebsites.net"
    ddns-path   = "/api/nic/update?hostname=%h&myip=%i"
}
```

### Command Line Testing

```bash
# Update with API key
curl -u "device:your-api-key" \
  "https://your-function.azurewebsites.net/api/nic/update?hostname=device.ddns-sandbox.example.com&myip=auto"

# Expected responses:
# good - IP updated successfully
# nochg - IP unchanged
# badauth - Invalid API key
# nohost - Hostname not found or not owned
```

## 🔐 Security Features

- **Per-hostname API keys**: Each hostname has unique keys
- **Azure AD integration**: Web interface requires authentication
- **Managed Identity**: No credentials in code
- **Key Vault**: Secure storage for sensitive data
- **Subdomain isolation**: Strict boundary enforcement
- **Admin role-based access**: Separate admin panel with elevated permissions

## 🛠️ Development

### Local Development

```bash
cd http
func start

# Access locally at http://localhost:7071
```

### Project Structure

```
azure-ddns/
├── http/                   # .NET 8 Azure Functions
│   ├── DdnsUpdateFunction.cs    # DDNS protocol endpoint
│   ├── HostnameManagementFunction.cs  # Web management
│   ├── AdminFunction.cs         # Admin panel
│   └── Services/                # Business logic
├── infra/                  # Bicep IaC templates
│   ├── main.bicep         # Main infrastructure
│   └── app/               # App-specific modules
└── Templates/             # HTML templates
```

## 📊 Admin Panel

Access at `/api/management` (requires Azure AD admin role):
- View all registered hostnames
- Monitor API key usage
- Track update statistics
- Manage user permissions

## 🙏 Credits

Inspired by the original [PowerShell Azure DDNS](https://github.com/jeff-winn/azure-ddns) implementation by [Jeff Winn](https://github.com/jeff-winn).

## 📄 License

MIT License - see [LICENSE](LICENSE.md) for details.

## 🤝 Contributing

Contributions welcome! Please open an issue first to discuss proposed changes.

## 🐛 Support

For issues or questions, please open an issue on [GitHub](https://github.com/primeinc/azure-ddns/issues).