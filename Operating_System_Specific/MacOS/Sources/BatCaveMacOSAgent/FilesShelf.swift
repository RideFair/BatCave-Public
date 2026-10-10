@preconcurrency import AppKit
import SwiftUI

struct FileShelfError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// The shelf accepts references to local regular files. The transfer layer must
/// reopen/revalidate these references before reading and hash the bytes it sends.
enum FileShelfSelection {
    static let maximumFiles = 32
    static let maximumFileBytes: Int64 = 1024 * 1024 * 1024
    static let maximumSelectionBytes: Int64 = 4 * 1024 * 1024 * 1024

    static func validate(_ urls: [URL]) throws -> [(url: URL, bytes: Int64)] {
        guard !urls.isEmpty, urls.count <= maximumFiles else {
            throw FileShelfError(message: "Choose between 1 and 32 regular files.")
        }
        var seen = Set<String>()
        var total: Int64 = 0
        var result: [(URL, Int64)] = []
        for original in urls {
            guard original.isFileURL, original.host == nil || original.host == "" || original.host == "localhost",
                  original.path.utf8.count <= 4096 else {
                throw FileShelfError(message: "Choose files stored on this Mac.")
            }
            // A URL may retain resource values from an earlier validation.
            // Recreate it from its path for every check; preserve /private/var
            // instead of NSURL standardization's /var system symlink spelling.
            let url = URL(fileURLWithPath: original.path)
            guard seen.insert(url.path).inserted else { continue }
            // Do not silently follow a symbolic link, including one in a parent
            // directory. Directory traversal is unnecessary for the regular-file v1.
            var component = url
            while component.path != "/" {
                let values = try component.resourceValues(forKeys: [.isSymbolicLinkKey])
                guard values.isSymbolicLink != true else {
                    throw FileShelfError(message: "Symbolic links are not supported. Choose the original file instead.")
                }
                component = URL(fileURLWithPath: component.deletingLastPathComponent().path)
            }
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isDirectoryKey, .isAliasFileKey, .fileSizeKey])
            guard values.isRegularFile == true, values.isDirectory != true, values.isAliasFile != true,
                  let length = values.fileSize, length >= 0 else {
                throw FileShelfError(message: "Folders, aliases, and special files are not supported yet.")
            }
            let bytes = Int64(length)
            guard bytes <= maximumFileBytes, total <= maximumSelectionBytes - bytes else {
                throw FileShelfError(message: "Choose files no larger than 1 GB each and 4 GB in total.")
            }
            guard FileManager.default.isReadableFile(atPath: url.path) else {
                throw FileShelfError(message: "BatCave cannot read \(url.lastPathComponent).")
            }
            total += bytes
            result.append((url, bytes))
        }
        return result
    }
}

@MainActor
struct FileShelfItem: Identifiable {
    let id: UUID
    let url: URL
    let name: String
    let bytes: Int64
    let received: Bool
    let portableIcon: PortableFileIcon?
    let icon: NSImage
}

/// No transport is implied by adding an item to the shelf. An offer callback
/// becomes available only after the agreed shared transfer protocol is connected.
@MainActor
final class FilesShelfModel: ObservableObject {
    @Published private(set) var items: [FileShelfItem] = []
    @Published var message: String?
    var onOffer: (([FileShelfItem]) -> Void)?
    var outgoingItems: [FileShelfItem] { items.filter { !$0.received } }
    var receivedItems: [FileShelfItem] { items.filter(\.received) }
    private var pendingIcons: [(id: UUID, url: URL)] = []
    private var iconWorker: Task<Void, Never>?

    private var genericIcon: NSImage {
        NSImage(systemSymbolName: "doc", accessibilityDescription: "File") ?? NSImage(size: NSSize(width: 32, height: 32))
    }

    private func scheduleIcon(for item: FileShelfItem) {
        // A single worker drains at most 32 pending references. Clearing and
        // repeatedly dropping files cannot spawn more workers or grow the queue.
        guard pendingIcons.count < FileShelfSelection.maximumFiles else { return }
        pendingIcons.append((item.id, item.url))
        guard iconWorker == nil else { return }
        iconWorker = Task.detached(priority: .utility) { [weak self] in
            while let next = await self?.nextIconWork() {
                let icon = try? PortableFileIcon.capture(fileURL: next.url)
                await self?.applyIcon(icon, to: next.id)
            }
        }
    }

    private func nextIconWork() -> (id: UUID, url: URL)? {
        while !pendingIcons.isEmpty {
            let work = pendingIcons.removeFirst()
            if items.contains(where: { $0.id == work.id }) { return work }
        }
        iconWorker = nil
        return nil
    }

    private func applyIcon(_ portable: PortableFileIcon?, to id: UUID) {
        guard let portable, let index = items.firstIndex(where: { $0.id == id }) else { return }
        let item = items[index]
        items[index] = FileShelfItem(id: item.id, url: item.url, name: item.name, bytes: item.bytes,
            received: item.received, portableIcon: portable, icon: portable.image)
    }

    func addLocalFiles(_ urls: [URL]) {
        do {
            let current = outgoingItems.map(\.url)
            let selection = try FileShelfSelection.validate(current + urls)
            let existing = Set(current.map(\.path))
            let newFiles = selection.filter { !existing.contains($0.url.path) }
            guard items.count + newFiles.count <= 64 else {
                throw FileShelfError(message: "Clear some shelf entries before adding more files.")
            }
            for (url, bytes) in newFiles {
                let item = FileShelfItem(id: UUID(), url: url, name: url.lastPathComponent,
                    bytes: bytes, received: false, portableIcon: nil, icon: genericIcon)
                items.append(item)
                scheduleIcon(for: item)
            }
            message = "Files added. You can check their icons here; they stay on this Mac."
        } catch { message = error.localizedDescription }
    }

    func chooseFiles() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.message = "Choose regular files to stage in BatCave. Your originals remain on this Mac."
        guard panel.runModal() == .OK else { return }
        addLocalFiles(panel.urls)
    }

    func revealReceivedFiles() {
        let urls = receivedItems.map(\.url)
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    /// Call only for a NEW file created by the receiving store after a transfer
    /// has completed and its size/content hash has been verified. This method
    /// validates local file shape, not authenticity. A source icon is shown in
    /// BatCave only; Finder icon metadata is unchanged by this method.
    @discardableResult
    func addVerifiedReceipt(at url: URL, sourceIcon: PortableFileIcon? = nil) throws -> FileShelfItem {
        guard items.count < 64 else { throw FileShelfError(message: "Clear some shelf entries before adding more files.") }
        let selection = try FileShelfSelection.validate([url])
        guard let file = selection.first else { throw FileShelfError(message: "The received file is unavailable.") }
        guard !items.contains(where: { $0.url == file.url }) else {
            throw FileShelfError(message: "This file is already on the shelf.")
        }
        let item = FileShelfItem(id: UUID(), url: file.url, name: file.url.lastPathComponent,
            bytes: file.bytes, received: true, portableIcon: sourceIcon,
            icon: sourceIcon?.image ?? genericIcon)
        items.append(item)
        if sourceIcon == nil { scheduleIcon(for: item) }
        message = "Drag a received file from the shelf into Finder to copy it."
        return item
    }

    /// Reserved for a later reviewed, explicit user action. No preview UI calls
    /// this helper. The item must be a NEW verified receiving-store file, never
    /// an existing user file. Native Finder icon propagation is not a v1 promise.
    @discardableResult
    func applySourceIconInFinder(to id: UUID) throws -> Bool {
        guard let item = items.first(where: { $0.id == id }), item.received,
              let icon = item.portableIcon else {
            throw FileShelfError(message: "A received file with a source icon is required.")
        }
        _ = try FileShelfSelection.validate([item.url])
        return NSWorkspace.shared.setIcon(icon.image, forFile: item.url.path, options: [])
    }

    func offerFiles() {
        guard let onOffer else {
            message = "Sending between computers is not available in this test build."
            return
        }
        do {
            _ = try FileShelfSelection.validate(outgoingItems.map(\.url))
            onOffer(outgoingItems)
        } catch { message = error.localizedDescription }
    }

    func clear() {
        pendingIcons.removeAll()
        items.removeAll()
        message = "Shelf cleared. Your original files remain on disk."
    }
}

@MainActor
struct FilesShelfView: View {
    @ObservedObject var model: FilesShelfModel
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Files preview").font(.title2.bold())
            Text("Drop files from Finder to check their icons. This test build keeps them on this Mac.")
                .foregroundStyle(.secondary)
            FileShelfNativeView(model: model)
                .frame(minWidth: 440, minHeight: 250)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.35)))
            if let message = model.message {
                Text(message).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Text("Sending between computers is not available yet. Regular files only, up to 32 files; 1 GB each and 4 GB total.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Choose files…") { model.chooseFiles() }
                Button("Clear shelf") { model.clear() }.disabled(model.items.isEmpty)
                Spacer()
                Button("Send files") { model.offerFiles() }
                    .disabled(model.outgoingItems.isEmpty || model.onOffer == nil)
            }
            if !model.receivedItems.isEmpty {
                Button("Show received files in Finder") { model.revealReceivedFiles() }
            }
        }
        .padding(22)
        .frame(minWidth: 500, minHeight: 440)
    }
}

@MainActor
private struct FileShelfNativeView: NSViewRepresentable {
    @ObservedObject var model: FilesShelfModel
    func makeNSView(context: Context) -> FileShelfScrollView {
        let scroll = FileShelfScrollView()
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.canvas.model = model
        scroll.documentView = scroll.canvas
        return scroll
    }
    func updateNSView(_ view: FileShelfScrollView, context: Context) {
        view.canvas.model = model
        view.canvas.setAccessibilityLabel("File shelf")
        view.canvas.setAccessibilityValue("\(model.items.count) files")
        view.updateDocumentSize()
        view.canvas.needsDisplay = true
    }
}

@MainActor
private final class FileShelfScrollView: NSScrollView {
    let canvas = FileShelfCanvas()
    override func layout() {
        super.layout()
        updateDocumentSize()
    }
    func updateDocumentSize() {
        canvas.frame.size = NSSize(width: contentSize.width,
            height: max(contentSize.height, CGFloat(canvas.model?.items.count ?? 0) * FileShelfCanvas.rowHeight + 20))
        canvas.updateAccessibilityRows()
    }
}

@MainActor
private final class FileShelfCanvas: NSView, NSDraggingSource {
    static let rowHeight: CGFloat = 58
    weak var model: FilesShelfModel?
    private var dragStart: NSEvent?
    private var dragItem: FileShelfItem?
    private var accessibilityRows: [UUID: NSAccessibilityElement] = [:]
    override var isFlipped: Bool { true }

    init() {
        super.init(frame: .zero)
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityHelp("Drop regular files here. Received files can be copied by dragging them into Finder.")
        registerForDraggedTypes([.fileURL])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func updateAccessibilityRows() {
        let items = model?.items ?? []
        let liveIds = Set(items.map(\.id))
        let changed = liveIds != Set(accessibilityRows.keys)
        accessibilityRows = accessibilityRows.filter { liveIds.contains($0.key) }
        var ordered: [NSAccessibilityElement] = []
        for (index, item) in items.enumerated() {
            let element: NSAccessibilityElement
            if let existing = accessibilityRows[item.id] {
                element = existing
            } else {
                let size = ByteCountFormatter.string(fromByteCount: item.bytes, countStyle: .file)
                let status = item.received ? "Received. Drag to Finder to copy." : "Staged locally. Transfer pending."
                guard let created = NSAccessibilityElement.element(withRole: .staticText, frame: .zero,
                    label: item.name + ", " + size + ". " + status, parent: self) as? NSAccessibilityElement else { continue }
                element = created
                accessibilityRows[item.id] = created
            }
            element.setAccessibilityFrameInParentSpace(NSRect(x: 10, y: 10 + CGFloat(index) * Self.rowHeight,
                width: max(0, bounds.width - 20), height: Self.rowHeight))
            ordered.append(element)
        }
        setAccessibilityChildren(ordered)
        if changed { NSAccessibility.post(element: self, notification: .layoutChanged) }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.controlBackgroundColor.setFill()
        bounds.fill()
        guard let items = model?.items, !items.isEmpty else {
            let text = "Drop regular files from Finder"
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 15), .foregroundColor: NSColor.secondaryLabelColor]
            let size = text.size(withAttributes: attributes)
            text.draw(at: NSPoint(x: max(10, (bounds.width - size.width) / 2), y: max(20, bounds.height / 2 - 10)), withAttributes: attributes)
            return
        }
        for (index, item) in items.enumerated() {
            let y = 10 + CGFloat(index) * Self.rowHeight
            let row = NSRect(x: 10, y: y, width: bounds.width - 20, height: Self.rowHeight)
            guard row.intersects(dirtyRect) else { continue }
            item.icon.draw(in: NSRect(x: 18, y: y + 9, width: 36, height: 36), from: .zero,
                operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
            let paragraph = NSMutableParagraphStyle()
            paragraph.lineBreakMode = .byTruncatingMiddle
            item.name.draw(in: NSRect(x: 66, y: y + 8, width: max(0, bounds.width - 82), height: 20),
                withAttributes: [.font: NSFont.systemFont(ofSize: 13, weight: .medium), .foregroundColor: NSColor.labelColor,
                    .paragraphStyle: paragraph])
            let size = ByteCountFormatter.string(fromByteCount: item.bytes, countStyle: .file)
            let status = item.received ? "Received · drag to Finder" : "Staged locally"
            (size + " · " + status).draw(at: NSPoint(x: 66, y: y + 31),
                withAttributes: [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.secondaryLabelColor])
        }
    }

    private func localURLs(from pasteboard: NSPasteboard) -> [URL]? {
        guard let count = pasteboard.pasteboardItems?.count, count > 0, count <= FileShelfSelection.maximumFiles,
              let values = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
              values.count == count else { return nil }
        return values
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard let urls = localURLs(from: sender.draggingPasteboard), !urls.isEmpty else { return [] }
        return .copy
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { draggingEntered(sender) }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { localURLs(from: sender.draggingPasteboard) != nil }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let urls = localURLs(from: sender.draggingPasteboard) else { return false }
        model?.addLocalFiles(urls)
        return true
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let index = Int(floor((point.y - 10) / Self.rowHeight))
        guard point.y >= 10, let items = model?.items, items.indices.contains(index), items[index].received else { return }
        dragStart = event
        dragItem = items[index]
    }
    override func mouseUp(with event: NSEvent) { dragStart = nil; dragItem = nil }
    override func mouseDragged(with event: NSEvent) {
        guard let initial = dragStart, let item = dragItem else { return }
        let initialPoint = convert(initial.locationInWindow, from: nil)
        let point = convert(event.locationInWindow, from: nil)
        guard hypot(point.x - initialPoint.x, point.y - initialPoint.y) >= 4 else { return }
        dragStart = nil; dragItem = nil
        do {
            _ = try FileShelfSelection.validate([item.url])
            let dragging = NSDraggingItem(pasteboardWriter: item.url as NSURL)
            dragging.setDraggingFrame(NSRect(x: point.x - 18, y: point.y - 18, width: 36, height: 36), contents: item.icon)
            beginDraggingSession(with: [dragging], event: initial, source: self)
        } catch { model?.message = error.localizedDescription }
    }
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func ignoreModifierKeys(for session: NSDraggingSession) -> Bool { true }
}
