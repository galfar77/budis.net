import AppKit
import PDFKit

/// Tisk obsahu z náhledu F3 přes standardní tiskový dialog macOS.
enum ViewerPrinter {
    @MainActor
    static func printContent(_ content: ViewerContent) {
        switch content.kind {
        case .pdf(let url):
            guard let doc = PDFDocument(url: url),
                  let op = doc.printOperation(for: NSPrintInfo.shared, scalingMode: .pageScaleDownToFit, autoRotate: true)
            else { return }
            op.jobTitle = content.title
            op.run()

        case .image(let image):
            let info = pageInfo()
            let size = fitted(image.size, in: printableSize(info))
            let view = NSImageView(frame: NSRect(origin: .zero, size: size))
            view.image = image
            view.imageScaling = .scaleProportionallyUpOrDown
            let op = NSPrintOperation(view: view, printInfo: info)
            op.jobTitle = content.title
            op.run()

        case .text(let text):
            let info = pageInfo()
            let width = printableSize(info).width
            let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
            view.isVerticallyResizable = true
            view.textContainer?.widthTracksTextView = true
            view.font = NSFont.monospacedSystemFont(ofSize: 10, weight: .regular)
            view.string = text
            view.sizeToFit()
            let op = NSPrintOperation(view: view, printInfo: info)
            op.jobTitle = content.title
            op.run()
        }
    }

    private static func pageInfo() -> NSPrintInfo {
        let info = (NSPrintInfo.shared.copy() as? NSPrintInfo) ?? NSPrintInfo.shared
        info.isHorizontallyCentered = true
        info.isVerticallyCentered = true
        return info
    }

    private static func printableSize(_ info: NSPrintInfo) -> NSSize {
        NSSize(width: info.paperSize.width - info.leftMargin - info.rightMargin,
               height: info.paperSize.height - info.topMargin - info.bottomMargin)
    }

    /// Zmenší obrázek, aby se vešel na stránku; větší než stránka se nezvětšuje.
    private static func fitted(_ size: NSSize, in box: NSSize) -> NSSize {
        guard size.width > 0, size.height > 0 else { return box }
        let scale = min(1, box.width / size.width, box.height / size.height)
        return NSSize(width: size.width * scale, height: size.height * scale)
    }
}
