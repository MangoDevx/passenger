#!/usr/bin/env python3
"""Writes the .DS_Store of the macOS disk image's top folder, and checks one names its background: Finder opens the
image as an icon window of a fixed size with no toolbar or sidebar, Passenger.app on the left and the Applications link
on the right to drag it onto, over a picture of the window's size (and its 2x) of an arrow from one to the other.

Written here, since Finder writes one only through AppleScript, which needs a window session and leave to automate it:
a buddy allocator's blocks holding one B-tree, a single leaf of records sorted by name then code. The picture is named
by a Carbon alias record, read from the image's own volume mounted while it is made (the compressed image keeps it).

python3 -I tools/dmg-layout.py write|check VOLUME NAME   (the image's top folder as mounted, and its volume's name)
"""
import os
import plistlib
import struct
import sys

APP = "Passenger.app"
# x, y, width, height on the screen, the title bar included, y counted up from the screen's bottom as Cocoa counts.
WINDOW = (200, 120, 600, 360)
ICON_SIZE = 128
# Each item's icon centre in the window. The background's folder, the volume's icon and the file system's event log
# folder are hidden, seen only by a person who shows hidden files, and kept below the window rather than between the
# two that matter.
ITEMS = {
    APP: (160, 165),
    "Applications": (440, 165),
    ".background": (100, 520),
    ".VolumeIcon.icns": (300, 520),
    ".fseventsd": (500, 520),
}
BACKGROUND_FOLDER, BACKGROUND_FILE = ".background", "background.tiff"
# The picture's size in points: the window's width by more than its height under the title bar, whose height differs
# from one macOS to the next, so no Finder shows white below it. Its 2x representation is twice each.
BACKGROUND_SIZE = (600, 400)
# Seconds from the Mac's epoch, 1904, to 1970's.
MAC_EPOCH = 2082844800

PAGE = 0x1000
# Offsets after the 4-byte prefix: the store's header (block 1), the bookkeeping (block 0) and the one leaf (block 2).
HEADER_AT, HEADER_SIZE = 0x20, 0x20
BOOKKEEPING_AT, BOOKKEEPING_SIZE = 0x800, 0x800
LEAF_AT, LEAF_SIZE = 0x1000, PAGE

# The alias record's tagged fields, written in the order Finder writes them (Background.alias).
TAG_FOLDER_NAME, TAG_FOLDER_IDS, TAG_CARBON_PATH = 0, 1, 2
TAG_FILE_NAME, TAG_VOLUME_NAME, TAG_VOLUME_DATE, TAG_FILE_DATE, TAG_PATH, TAG_MOUNT_POINT = 14, 15, 16, 17, 18, 19
EJECTABLE_DISK = 5
# The alias's fixed part after its 8-byte header: what it names (0, a file), the volume's name and creation date,
# its file system and kind of disk, the parent folder's catalog ID, the file's name, catalog ID and creation date, its
# type and creator, the folder levels from and to (-1: not counted), the volume's attributes and file system ID.
ALIAS_FIXED = ">h28pI2shI64pII4s4shhI2s10s"


def record(name, code, kind, value):
    """One record: its file name as UTF-16, its four-character code and type, then the value in that type's form."""
    data = struct.pack(">I", len(name)) + name.encode("utf-16-be") + code.encode("ascii") + kind.encode("ascii")
    if kind == "blob":
        return data + struct.pack(">I", len(value)) + value
    if kind == "long":
        return data + struct.pack(">I", value)
    if kind == "type":
        return data + value.encode("ascii")
    raise ValueError(kind)


class Background:
    """The background picture on the mounted volume: what its alias says of it, read from the file system."""

    def __init__(self, volume, name):
        if len(name.encode("mac-roman")) > 27:
            raise ValueError(f"the volume's name '{name}' is longer than an alias record's 27 characters")
        self.name = name
        self.folder = os.path.join(volume, BACKGROUND_FOLDER)
        self.path = os.path.join(self.folder, BACKGROUND_FILE)
        if not os.path.isfile(self.path):
            raise FileNotFoundError(f"the image has no {BACKGROUND_FOLDER}/{BACKGROUND_FILE}")
        # On HFS+ a file's inode number is its catalog node ID, and the mount point's creation time the volume's.
        self.volume_made = os.stat(volume).st_birthtime + MAC_EPOCH
        self.made = os.stat(self.path).st_birthtime + MAC_EPOCH
        self.folder_id = os.stat(self.folder).st_ino
        self.id = os.stat(self.path).st_ino
        self.carbon_path = f"{name}:{BACKGROUND_FOLDER}:{BACKGROUND_FILE}"
        self.posix_path = f"/{BACKGROUND_FOLDER}/{BACKGROUND_FILE}"
        self.mount_point = f"/Volumes/{name}"

    def alias(self):
        """The picture's alias record, version 2, as Finder writes one into icvp's backgroundImageAlias."""
        fixed = struct.pack(ALIAS_FIXED, 0, self.name.encode("mac-roman"), int(self.volume_made), b"H+", EJECTABLE_DISK,
                            self.folder_id, BACKGROUND_FILE.encode("mac-roman"), self.id, int(self.made), b"\0" * 4,
                            b"\0" * 4, -1, -1, 0, b"\0" * 2, b"\0" * 10)
        fields = [
            (TAG_FOLDER_NAME, BACKGROUND_FOLDER.encode("mac-roman")),
            (TAG_VOLUME_DATE, struct.pack(">Q", int(self.volume_made * 65536))),
            (TAG_FILE_DATE, struct.pack(">Q", int(self.made * 65536))),
            (TAG_FOLDER_IDS, struct.pack(">I", self.folder_id)),
            (TAG_CARBON_PATH, self.carbon_path.encode("mac-roman")),
            (TAG_FILE_NAME, unicode_name(BACKGROUND_FILE)),
            (TAG_VOLUME_NAME, unicode_name(self.name)),
            (TAG_PATH, self.posix_path.encode()),
            (TAG_MOUNT_POINT, self.mount_point.encode()),
        ]
        body = fixed + b"".join(struct.pack(">hh", tag, len(value)) + value + b"\0" * (len(value) % 2)
                                for tag, value in fields) + struct.pack(">hh", -1, 0)
        return struct.pack(">4shh", b"\0" * 4, 8 + len(body), 2) + body


def unicode_name(text):
    """A name as an alias's Unicode fields hold it: its length in UTF-16 units, then the units."""
    units = text.encode("utf-16-be")
    return struct.pack(">H", len(units) // 2) + units


def window_records(background):
    x, y, width, height = WINDOW
    browser = {
        "ContainerShowSidebar": False,
        "PreviewPaneVisibility": False,
        "ShowPathbar": False,
        "ShowSidebar": False,
        "ShowStatusBar": False,
        "ShowTabView": False,
        "ShowToolbar": False,
        "SidebarWidth": 180,
        "WindowBounds": f"{{{{{x}, {y}}}, {{{width}, {height}}}}}",
    }
    icons = {
        "arrangeBy": "none",
        "backgroundColorBlue": 1.0,
        "backgroundColorGreen": 1.0,
        "backgroundColorRed": 1.0,
        # A picture, named by the alias.
        "backgroundImageAlias": background.alias(),
        "backgroundType": 2,
        "gridOffsetX": 0.0,
        "gridOffsetY": 0.0,
        "gridSpacing": 100.0,
        "iconSize": float(ICON_SIZE),
        "labelOnBottom": True,
        "scrollPositionX": 0.0,
        "scrollPositionY": 0.0,
        "showIconPreview": False,
        "showItemInfo": False,
        "textSize": 13.0,
        "viewOptionsVersion": 1,
    }
    yield ".", "bwsp", "blob", plistlib.dumps(browser, fmt=plistlib.FMT_BINARY, sort_keys=True)
    yield ".", "icvl", "type", "icnv"
    yield ".", "icvp", "blob", plistlib.dumps(icons, fmt=plistlib.FMT_BINARY, sort_keys=True)
    yield ".", "vSrn", "long", 1
    for name, (left, top) in ITEMS.items():
        # The position, then what Finder writes after it in every one: -1 and 0xffff0000.
        yield name, "Iloc", "blob", struct.pack(">II", left, top) + b"\xff" * 6 + b"\x00" * 2


def address(offset, size):
    """A block's entry in the allocator's table: its offset, with log2 of its size in the low five bits."""
    return offset | (size.bit_length() - 1)


def store(background):
    # Finder compares names without case, then codes.
    records = sorted(window_records(background), key=lambda r: (r[0].lower(), r[1]))
    leaf = struct.pack(">II", 0, len(records)) + b"".join(record(*r) for r in records)
    if len(leaf) > LEAF_SIZE:
        raise ValueError(f"the layout's records ({len(leaf)} bytes) do not fit one {LEAF_SIZE}-byte leaf")
    # The store: its root node (block 2), no levels above the leaf, the record count, one node, the page size.
    header = struct.pack(">IIIII", 2, 0, len(records), 1, PAGE)

    blocks = [address(BOOKKEEPING_AT, BOOKKEEPING_SIZE), address(HEADER_AT, HEADER_SIZE), address(LEAF_AT, LEAF_SIZE)]
    bookkeeping = struct.pack(">II", len(blocks), 0) + struct.pack(">256I", *(blocks + [0] * (256 - len(blocks))))
    bookkeeping += struct.pack(">I", 1) + bytes([4]) + b"DSDB" + struct.pack(">I", 1)
    # The free blocks, one list per size: every buddy of the blocks in use, up to the whole 2^31-byte space.
    used = {HEADER_AT, BOOKKEEPING_AT, LEAF_AT}
    for power in range(32):
        free = [1 << power] if 6 <= power <= 30 and (1 << power) not in used else []
        bookkeeping += struct.pack(">I", len(free)) + b"".join(struct.pack(">I", f) for f in free)

    space = bytearray(LEAF_AT + LEAF_SIZE)
    space[0:4] = b"Bud1"
    space[4:16] = struct.pack(">III", BOOKKEEPING_AT, BOOKKEEPING_SIZE, BOOKKEEPING_AT)
    space[HEADER_AT:HEADER_AT + len(header)] = header
    space[BOOKKEEPING_AT:BOOKKEEPING_AT + len(bookkeeping)] = bookkeeping
    space[LEAF_AT:LEAF_AT + len(leaf)] = leaf
    return struct.pack(">I", 1) + bytes(space)


def read_store(data):
    """Every record of a .DS_Store as (name, code, value), walked from the allocator's table as Finder reads it rather
    than where write puts them, with each blob's bytes and each other value's raw bytes."""
    if data[:8] != b"\0\0\0\1Bud1":
        raise ValueError("it is not a .DS_Store (no Bud1 header)")
    space = data[4:]
    at, size = struct.unpack(">II", space[4:12])
    bookkeeping = space[at:at + size]
    count = struct.unpack(">I", bookkeeping[:4])[0]
    blocks = struct.unpack(f">{count}I", bookkeeping[8:8 + 4 * count])
    at = 8 + 4 * 256
    directories = {}
    for _ in range(struct.unpack(">I", bookkeeping[at:at + 4])[0]):
        length = bookkeeping[at + 4]
        name = bookkeeping[at + 5:at + 5 + length]
        directories[name] = struct.unpack(">I", bookkeeping[at + 5 + length:at + 9 + length])[0]
        at += 5 + length

    def block(number):
        return space[blocks[number] & ~0x1F:(blocks[number] & ~0x1F) + (1 << (blocks[number] & 0x1F))]

    sizes = {"long": 4, "shor": 4, "bool": 1, "type": 4, "comp": 8, "dutc": 8}

    def node(number):
        data = block(number)
        last, count = struct.unpack(">II", data[:8])
        at = 8
        for _ in range(count):
            if last:
                yield from node(struct.unpack(">I", data[at:at + 4])[0])
                at += 4
            length = struct.unpack(">I", data[at:at + 4])[0]
            name = data[at + 4:at + 4 + 2 * length].decode("utf-16-be")
            at += 4 + 2 * length
            code, kind = data[at:at + 4].decode("ascii"), data[at + 4:at + 8].decode("ascii")
            at += 8
            if kind in sizes:
                value, at = data[at:at + sizes[kind]], at + sizes[kind]
            elif kind in ("blob", "ustr"):
                length = struct.unpack(">I", data[at:at + 4])[0] * (2 if kind == "ustr" else 1)
                value, at = data[at + 4:at + 4 + length], at + 4 + length
            else:
                raise ValueError(f"its record {name} {code} has a type, {kind}, Finder does not write")
            yield name, code, value
        if last:
            yield from node(last)

    yield from node(struct.unpack(">I", block(directories[b"DSDB"])[:4])[0])


def read_alias(data):
    """An alias record's fixed fields by name and its tagged ones by tag, checking its length against its tags'."""
    _, length, version = struct.unpack(">4shh", data[:8])
    if version != 2 or length != len(data):
        raise ValueError(f"the background's alias is a version {version} record of {length} bytes in {len(data)}, "
                         "not version 2 and whole")
    fixed = struct.unpack(ALIAS_FIXED, data[8:150])
    names = ("kind", "volume", "volume_made", "file_system", "disk", "folder_id", "file", "id", "made")
    fields = dict(zip(names, fixed))
    at = 150
    while True:
        tag, length = struct.unpack(">hh", data[at:at + 4])
        if tag == -1:
            break
        fields[tag] = data[at + 4:at + 4 + length]
        at += 4 + length + length % 2
    if at + 4 != len(data):
        raise ValueError("the background's alias does not end where its length says")
    return fields


def tiff_sizes(data):
    """Each picture in a TIFF, as (width, height) in pixels, in the order its directories chain them."""
    order = {b"MM": ">", b"II": "<"}.get(data[:2])
    if order is None or struct.unpack(order + "H", data[2:4])[0] != 42:
        raise ValueError("the background is not a TIFF")
    sizes = []
    at = struct.unpack(order + "I", data[4:8])[0]
    while at:
        count = struct.unpack(order + "H", data[at:at + 2])[0]
        size = {}
        for entry in range(count):
            tag, kind, _, value = struct.unpack(order + "HHI4s", data[at + 2 + 12 * entry:at + 14 + 12 * entry])
            if tag in (256, 257):
                size[tag] = struct.unpack(order + ("H" if kind == 3 else "I"), value[:2 if kind == 3 else 4])[0]
        sizes.append((size.get(256), size.get(257)))
        at = struct.unpack(order + "I", data[at + 2 + 12 * count:at + 6 + 12 * count])[0]
    return sizes


def check(volume, name):
    background = Background(volume, name)
    width, height = BACKGROUND_SIZE
    if width != WINDOW[2] or height < WINDOW[3]:
        sys.exit(f"dmg-layout: the background is {width}x{height}, where the {WINDOW[2]}x{WINDOW[3]} window needs its "
                 "width and at least its height")
    with open(background.path, "rb") as picture:
        sizes = tiff_sizes(picture.read())
    if sizes != [(width, height), (2 * width, 2 * height)]:
        sys.exit(f"dmg-layout: the background holds pictures of {sizes} pixels, not {width}x{height} and its 2x, "
                 f"{2 * width}x{2 * height}")
    with open(os.path.join(volume, ".DS_Store"), "rb") as file:
        records = {(n, code): value for n, code, value in read_store(file.read())}
    icons = plistlib.loads(records.get((".", "icvp"), plistlib.dumps({})))
    if icons.get("backgroundType") != 2 or "backgroundImageAlias" not in icons:
        sys.exit("dmg-layout: the .DS_Store's icon view has no background picture")
    fields = read_alias(icons["backgroundImageAlias"])
    said = {
        "the volume's name": (fields["volume"].decode("mac-roman"), name),
        "the volume's Unicode name": (fields.get(TAG_VOLUME_NAME), unicode_name(name)),
        "the volume's creation date": (fields["volume_made"], int(background.volume_made)),
        "the picture's folder's catalog ID": (fields["folder_id"], background.folder_id),
        "the picture's name": (fields["file"].decode("mac-roman"), BACKGROUND_FILE),
        "the picture's catalog ID": (fields["id"], background.id),
        "the picture's Carbon path": (fields.get(TAG_CARBON_PATH), background.carbon_path.encode("mac-roman")),
        "the picture's path": (fields.get(TAG_PATH), background.posix_path.encode()),
        "the volume's mount point": (fields.get(TAG_MOUNT_POINT), background.mount_point.encode()),
    }
    for what, (alias, actual) in said.items():
        if alias != actual:
            sys.exit(f"dmg-layout: the background's alias gives {what} as {alias!r}, not {actual!r}")
    if (BACKGROUND_FOLDER, "Iloc") not in records:
        sys.exit(f"dmg-layout: the .DS_Store gives {BACKGROUND_FOLDER} no place below the window")


def main():
    if len(sys.argv) != 4 or sys.argv[1] not in ("write", "check"):
        sys.exit("usage: dmg-layout.py write|check VOLUME NAME (the image's top folder, mounted; its volume's name)")
    _, command, volume, name = sys.argv
    try:
        if command == "write":
            with open(os.path.join(volume, ".DS_Store"), "wb") as out:
                out.write(store(Background(volume, name)))
        else:
            check(volume, name)
    except (OSError, ValueError, KeyError, IndexError, struct.error) as error:
        sys.exit(f"dmg-layout: {command}: {error}")


main()
