import SwiftUI

/// Skutečné „Liquid Glass“ (macOS 26 a Xcode 26). Ve starších prostředích se nic nemění: kód se přeloží jen s novým
/// překladačem a spustí jen na macOS 26; jinak platí rozostřené pozadí okna z VisualEffectBackground.
extension View {
    @ViewBuilder
    func liquidGlass(cornerRadius: CGFloat = 8) -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            self.glassEffect(.regular, in: RoundedRectangle(cornerRadius: cornerRadius))
        } else {
            self
        }
        #else
        self
        #endif
    }

    @ViewBuilder
    func functionButtonStyle() -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            self.buttonStyle(.glass)
        } else {
            self.buttonStyle(.bordered)
        }
        #else
        self.buttonStyle(.bordered)
        #endif
    }
}
