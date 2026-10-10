import SwiftUI

/// Okno nápovědy: seznam odrážek s nadpisy (co je nového, přehled funkcí).
struct InfoSheet: View {
    let title: String
    let lines: [String]
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                        if line.hasPrefix("# ") {
                            Text(String(L(line).dropFirst(2))).font(.system(size: 14, weight: .semibold)).padding(.top, 8)
                        } else {
                            Text("•  " + L(line)).fixedSize(horizontal: false, vertical: true).padding(.leading, 8)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 380)
            HStack {
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 560)
    }
}

/// O aplikaci: název, verze (commit sestavení), odkaz na projekt a tlačítko pro kontrolu aktualizací.
struct AboutSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    private var buildText: String {
        let commit = Updater.currentCommit
        return commit.isEmpty ? L("Vývojové sestavení (bez označení verze)")
                              : L("Sestavení z commitu: \(String(commit.prefix(12)))")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Budis Commander")).font(.title2.bold())
            Text(L("Dvoupanelový správce souborů"))
            Text(buildText).foregroundStyle(.secondary)
            Text(L("Projekt: \("https://github.com/" + Updater.repo)")).foregroundStyle(.secondary)
            Text(String(L("# Nové").dropFirst(2))).font(.system(size: 14, weight: .semibold)).padding(.top, 8)
            ForEach(Array(InfoTexts.news.dropFirst().prefix(5).enumerated()), id: \.offset) { _, line in
                Text("•  " + L(line)).fixedSize(horizontal: false, vertical: true).padding(.leading, 8)
            }
            HStack {
                Button(L("Zkontrolovat aktualizace")) {
                    dismiss()
                    Task { await model.checkForUpdate() }
                }
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(.top, 8)
        }
        .padding(16)
        .frame(width: 520)
    }
}
