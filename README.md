# Azure Dynamic DNS Service

A Dynamic DNS (DDNS) service for Azure Functions that provides DynDNS2 protocol compatibility for updating DNS records in Azure DNS zones. Compatible with UniFi Dream Machines and other routers supporting custom DDNS providers.

## 🙏 Credits & Inspiration

This project is a refactoring and modernization of the original [PowerShell Azure DDNS](https://github.com/jeff-winn/azure-ddns) implementation by [Jeff Winn](https://github.com/jeff-winn). The original PowerShell function provides the foundation for this project.

### Planned Improvements (In Progress)
- Migration from PowerShell to .NET 8 isolated process model
- Enhanced security with Azure Managed Identities
- Infrastructure as Code using Bicep and Azure Developer CLI
- Production-ready architecture with proper error handling and logging

## Overview

This service bridges the gap between consumer routers (like UniFi Dream Machines) and Azure DNS, allowing automatic DNS updates when your public IP address changes. It implements the DynDNS2 protocol, making it compatible with most DDNS clients including Inadyn.

## Current Features (PowerShell Implementation)

- **DynDNS2 Protocol Support**: Compatible with UniFi, Inadyn, and other standard DDNS clients  
- **Azure DNS Integration**: Direct updates to Azure-hosted DNS zones
- **Basic Authentication**: Username/password authentication
- **Azure Functions**: Runs on consumption plan for cost effectiveness

## Current Setup (PowerShell Version)

### Prerequisites

- Azure subscription with an existing DNS zone
- Azure Functions runtime with PowerShell support

### Deploy to Azure

1. Create a new Azure Function App with PowerShell runtime
2. Configure Application Settings:
   - `AppUsername` - Username for authentication
   - `AppPassword` - Password for authentication  
   - `DnsZoneRGName` - Resource Group containing your DNS Zone
3. Enable System or User Assigned Managed Identity
4. Grant the identity `DNS Zone Contributor` role on your DNS Zone
5. Deploy the PowerShell function code from the `Get-DDnsUpdate` folder

## Configuration

### Function App Settings

| Setting | Description | Example |
|---------|-------------|---------|
| `AppUsername` | Username for Basic Authentication | `ddns-user` |
| `AppPassword` | Password for Basic Authentication | `SecurePassword123!` |
| `DnsZoneRGName` | Resource Group containing DNS Zone | `rg-dns-prod` |
| `DnsZoneName` | Azure DNS Zone name | `example.com` |

**Note**: Passwords cannot contain colons (`:`) due to Basic Authentication format requirements.

### UniFi Dream Machine Configuration

1. Access your UniFi console
2. Navigate to Settings → Internet → WAN
3. Under Dynamic DNS, create a new service:
   - **Service**: `custom`
   - **Hostname**: `home.yourdomain.com`
   - **Username**: Your `AppUsername`
   - **Password**: Your `AppPassword`
   - **Server**: `your-function-app.azurewebsites.net/nic/update?hostname=%h&myip=%i`

### Inadyn Configuration

Create or edit `/etc/inadyn.conf`:

```conf
custom your-ddns.azurewebsites.net:1 {
    hostname    = "home.yourdomain.com"
    username    = "your-username"
    password    = "your-password"
    ddns-server = "your-ddns.azurewebsites.net"
    ddns-path   = "/nic/update?hostname=%h&myip=%i"
}
```

## Testing

Test your configuration with curl:

```bash
curl -u username:password "https://your-ddns.azurewebsites.net/nic/update?hostname=home.yourdomain.com&myip=1.2.3.4"
```

Expected responses:
- `good` - IP updated successfully
- `nochg` - IP unchanged
- `badauth` - Authentication failed
- `notfqdn` - Invalid hostname format

## Development (.NET Migration - In Progress)

The .NET 8 migration is currently under development. See [docs/REFACTOR_PLAN.md](docs/REFACTOR_PLAN.md) for the detailed migration plan.

### Project Structure

```
azure-ddns/
├── Get-DDnsUpdate/         # Current PowerShell function
│   ├── function.json       # Function bindings
│   └── run.ps1            # Main handler
├── http/                   # .NET 8 template (migration target)
│   └── http.csproj        # Project file
├── infra/                  # Bicep IaC templates
│   └── main.bicep         # Infrastructure definition
└── docs/                   # Documentation
    └── REFACTOR_PLAN.md   # Migration plan
```

## Roadmap

### Phase 1: MVP (.NET Migration)
- [ ] Port PowerShell logic to .NET 8
- [ ] Implement Basic Authentication middleware
- [ ] Azure DNS SDK integration
- [ ] Bicep/azd deployment automation

### Phase 2: Enhanced Features
- [ ] Managed Identity authentication
- [ ] IPv6 (AAAA record) support
- [ ] Multiple hostname updates
- [ ] Comprehensive error responses

### Phase 3: Production Hardening
- [ ] Azure Key Vault integration
- [ ] Application Insights monitoring
- [ ] Rate limiting
- [ ] Automated testing

## Contributing

Contributions are welcome! Please read our [Contributing Guide](CONTRIBUTING.md) for details.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE.md) file for details.

## Acknowledgments

- Original PowerShell implementation by [Jeff Winn](https://github.com/jeff-winn)
- Microsoft Azure Functions team for the .NET template
- The Inadyn and UniFi communities for DDNS protocol documentation

## Support

For issues, questions, or contributions, please open an issue on [GitHub](https://github.com/primeinc/azure-ddns/issues).