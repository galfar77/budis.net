import Foundation

/// Hledá v místní síti počítače nabízející SMB sdílení (Bonjour).
final class LANBrowser: NSObject, ObservableObject, NetServiceBrowserDelegate, NetServiceDelegate {
    struct Host: Identifiable, Hashable {
        let id: String
        let name: String
        let address: String
    }

    @Published var hosts: [Host] = []
    private let browser = NetServiceBrowser()
    private var services: [NetService] = []

    func start() {
        browser.delegate = self
        browser.searchForServices(ofType: "_smb._tcp.", inDomain: "local.")
    }

    func stop() {
        browser.stop()
        services.removeAll()
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        service.delegate = self
        services.append(service)
        service.resolve(withTimeout: 5)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        services.removeAll { $0 == service }
        hosts.removeAll { $0.id == service.name }
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard var host = sender.hostName else { return }
        while host.hasSuffix(".") { host.removeLast() }
        let entry = Host(id: sender.name, name: sender.name, address: host)
        if !hosts.contains(entry) {
            hosts.append(entry)
            hosts.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
    }
}
