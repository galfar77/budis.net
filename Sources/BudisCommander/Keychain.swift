import Foundation
import Security

/// Ukládání hesel k serverům do Klíčenky.
enum Keychain {
    private static let service = "net.budis.commander"

    private static func base(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func set(_ password: String, account: String) {
        SecItemDelete(base(account) as CFDictionary)
        var item = base(account)
        item[kSecValueData as String] = Data(password.utf8)
        SecItemAdd(item as CFDictionary, nil)
    }

    static func get(account: String) -> String? {
        var query = base(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    static func delete(account: String) {
        SecItemDelete(base(account) as CFDictionary)
    }
}
