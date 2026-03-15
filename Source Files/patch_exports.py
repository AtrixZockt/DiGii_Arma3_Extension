"""
Patches a PE DLL to add stdcall-decorated export aliases.

For Arma 3 x86 extensions, the Publisher requires stdcall-decorated
export names (_RVExtension@12) alongside the undecorated names.
The MSVC linker always strips stdcall decoration from exports,
so we patch the PE export table directly after linking.
"""
import struct
import sys
import os

def patch_pe_exports(filepath, extra_exports):
    """
    Add extra named exports to a PE DLL that alias existing exports.

    extra_exports: list of (new_name, existing_name) tuples
        e.g. [("_RVExtension@12", "RVExtension")]
    """
    with open(filepath, 'rb') as f:
        data = bytearray(f.read())

    # Parse DOS header
    if data[:2] != b'MZ':
        raise ValueError("Not a valid PE file")
    e_lfanew = struct.unpack_from('<I', data, 0x3C)[0]

    # Parse PE signature
    if data[e_lfanew:e_lfanew+4] != b'PE\x00\x00':
        raise ValueError("Invalid PE signature")

    # COFF header
    num_sections = struct.unpack_from('<H', data, e_lfanew + 6)[0]
    optional_hdr_size = struct.unpack_from('<H', data, e_lfanew + 20)[0]

    # Optional header
    opt_offset = e_lfanew + 24
    magic = struct.unpack_from('<H', data, opt_offset)[0]

    if magic == 0x10B:  # PE32
        export_dir_dd_offset = opt_offset + 96
        file_alignment_offset = opt_offset + 36
        size_of_image_offset = opt_offset + 56
    elif magic == 0x20B:  # PE32+
        export_dir_dd_offset = opt_offset + 112
        file_alignment_offset = opt_offset + 36
        size_of_image_offset = opt_offset + 56
    else:
        raise ValueError(f"Unknown PE magic: 0x{magic:04X}")

    export_dir_rva = struct.unpack_from('<I', data, export_dir_dd_offset)[0]
    export_dir_size = struct.unpack_from('<I', data, export_dir_dd_offset + 4)[0]
    file_alignment = struct.unpack_from('<I', data, file_alignment_offset)[0]

    if export_dir_rva == 0:
        raise ValueError("No export directory")

    # Parse section headers
    sections_offset = opt_offset + optional_hdr_size
    sections = []
    for i in range(num_sections):
        sec_off = sections_offset + i * 40
        name = data[sec_off:sec_off+8].rstrip(b'\x00').decode('ascii', errors='replace')
        vsize = struct.unpack_from('<I', data, sec_off + 8)[0]
        vrva = struct.unpack_from('<I', data, sec_off + 12)[0]
        raw_size = struct.unpack_from('<I', data, sec_off + 16)[0]
        raw_ptr = struct.unpack_from('<I', data, sec_off + 20)[0]
        sections.append({
            'name': name, 'vsize': vsize, 'vrva': vrva,
            'raw_size': raw_size, 'raw_ptr': raw_ptr, 'header_off': sec_off
        })

    def rva_to_offset(rva):
        for sec in sections:
            if sec['vrva'] <= rva < sec['vrva'] + sec['vsize']:
                return sec['raw_ptr'] + (rva - sec['vrva'])
        return None

    def offset_to_rva(off):
        for sec in sections:
            if sec['raw_ptr'] <= off < sec['raw_ptr'] + sec['raw_size']:
                return sec['vrva'] + (off - sec['raw_ptr'])
        return None

    # Parse export directory
    exp_off = rva_to_offset(export_dir_rva)
    num_functions = struct.unpack_from('<I', data, exp_off + 20)[0]
    num_names = struct.unpack_from('<I', data, exp_off + 24)[0]
    ordinal_base = struct.unpack_from('<I', data, exp_off + 16)[0]
    addr_table_rva = struct.unpack_from('<I', data, exp_off + 28)[0]
    name_table_rva = struct.unpack_from('<I', data, exp_off + 32)[0]
    ordinal_table_rva = struct.unpack_from('<I', data, exp_off + 36)[0]

    addr_table_off = rva_to_offset(addr_table_rva)
    name_table_off = rva_to_offset(name_table_rva)
    ordinal_table_off = rva_to_offset(ordinal_table_rva)

    # Read existing exports
    existing_exports = {}
    for i in range(num_names):
        name_rva = struct.unpack_from('<I', data, name_table_off + i * 4)[0]
        name_off = rva_to_offset(name_rva)
        end = data.index(b'\x00', name_off)
        name = data[name_off:end].decode('ascii')
        ordinal = struct.unpack_from('<H', data, ordinal_table_off + i * 2)[0]
        func_rva = struct.unpack_from('<I', data, addr_table_off + ordinal * 4)[0]
        existing_exports[name] = {'ordinal': ordinal, 'func_rva': func_rva}

    print(f"Existing exports: {list(existing_exports.keys())}")

    # Resolve extra exports
    new_entries = []
    for new_name, existing_name in extra_exports:
        if existing_name not in existing_exports:
            print(f"WARNING: '{existing_name}' not found in exports, skipping '{new_name}'")
            continue
        if new_name in existing_exports:
            print(f"'{new_name}' already exported, skipping")
            continue
        new_entries.append((new_name, existing_exports[existing_name]['ordinal']))

    if not new_entries:
        print("No new exports to add")
        return

    # Find the export section
    export_section = None
    for sec in sections:
        if sec['vrva'] <= export_dir_rva < sec['vrva'] + sec['vsize']:
            export_section = sec
            break

    if not export_section:
        raise ValueError("Could not find export section")

    # We'll append new data at the end of the export section's raw data
    # First, find how much of the section is actually used
    section_end_raw = export_section['raw_ptr'] + export_section['raw_size']

    # Build new export table data:
    # 1. New name strings (null-terminated)
    # 2. New name pointer table (replaces old one, includes new entries)
    # 3. New ordinal table (replaces old one, includes new entries)

    # Collect all names (existing + new), sorted alphabetically (PE requirement)
    all_names = []
    for i in range(num_names):
        name_rva = struct.unpack_from('<I', data, name_table_off + i * 4)[0]
        name_off = rva_to_offset(name_rva)
        end = data.index(b'\x00', name_off)
        name = data[name_off:end].decode('ascii')
        ordinal = struct.unpack_from('<H', data, ordinal_table_off + i * 2)[0]
        all_names.append((name, ordinal, name_rva))  # Keep existing name RVA

    for new_name, ordinal in new_entries:
        all_names.append((new_name, ordinal, None))  # None = needs new string

    # Sort alphabetically (binary search requirement for PE exports)
    all_names.sort(key=lambda x: x[0])

    new_num_names = len(all_names)

    # Calculate space needed at end of section
    # New name strings
    new_strings_size = sum(len(n) + 1 for n, _, rva in all_names if rva is None)
    # New name pointer table (4 bytes per entry)
    new_name_table_size = new_num_names * 4
    # New ordinal table (2 bytes per entry)
    new_ordinal_table_size = new_num_names * 2
    total_new_size = new_strings_size + new_name_table_size + new_ordinal_table_size

    # Check if there's enough padding at the end of the section
    # Find the highest used offset in the section
    used_end = export_section['raw_ptr'] + export_section['vsize']
    available = section_end_raw - used_end

    if available < total_new_size:
        # Need to grow the section - extend the file
        padding_needed = total_new_size - available
        # Align to file alignment
        padding_needed = ((padding_needed + file_alignment - 1) // file_alignment) * file_alignment

        # Extend the file at the end of the section
        insert_point = section_end_raw
        data[insert_point:insert_point] = b'\x00' * padding_needed

        # Update section raw_size
        export_section['raw_size'] += padding_needed
        struct.pack_into('<I', data, export_section['header_off'] + 16, export_section['raw_size'])

        # Update vsize if needed
        if export_section['vsize'] < export_section['raw_size']:
            export_section['vsize'] = export_section['raw_size']
            struct.pack_into('<I', data, export_section['header_off'] + 8, export_section['vsize'])

        # Update SizeOfImage
        size_of_image = struct.unpack_from('<I', data, size_of_image_offset)[0]
        section_alignment = struct.unpack_from('<I', data, opt_offset + 32)[0]
        new_image_size = 0
        for sec in sections:
            sec_end = sec['vrva'] + sec['vsize']
            new_image_size = max(new_image_size, sec_end)
        new_image_size = ((new_image_size + section_alignment - 1) // section_alignment) * section_alignment
        struct.pack_into('<I', data, size_of_image_offset, new_image_size)

        # Shift all sections after the export section
        for sec in sections:
            if sec['raw_ptr'] > export_section['raw_ptr']:
                sec['raw_ptr'] += padding_needed
                struct.pack_into('<I', data, sec['header_off'] + 20, sec['raw_ptr'])

        section_end_raw = export_section['raw_ptr'] + export_section['raw_size']

    # Now write new data at the end of the used portion of the section
    write_offset = used_end

    # Write new name strings
    new_name_rvas = {}
    for name, ordinal, existing_rva in all_names:
        if existing_rva is None:
            rva = offset_to_rva(write_offset)
            name_bytes = name.encode('ascii') + b'\x00'
            data[write_offset:write_offset + len(name_bytes)] = name_bytes
            new_name_rvas[name] = rva
            write_offset += len(name_bytes)

    # Write new name pointer table
    new_name_table_rva = offset_to_rva(write_offset)
    for name, ordinal, existing_rva in all_names:
        rva = existing_rva if existing_rva is not None else new_name_rvas[name]
        struct.pack_into('<I', data, write_offset, rva)
        write_offset += 4

    # Write new ordinal table
    new_ordinal_table_rva = offset_to_rva(write_offset)
    for name, ordinal, existing_rva in all_names:
        struct.pack_into('<H', data, write_offset, ordinal)
        write_offset += 2

    # Update export directory
    struct.pack_into('<I', data, exp_off + 24, new_num_names)  # NumberOfNames
    struct.pack_into('<I', data, exp_off + 32, new_name_table_rva)  # AddressOfNames
    struct.pack_into('<I', data, exp_off + 36, new_ordinal_table_rva)  # AddressOfNameOrdinals

    # Update export directory size in data directory
    new_export_size = write_offset - rva_to_offset(export_dir_rva)
    struct.pack_into('<I', data, export_dir_dd_offset + 4, new_export_size)

    # Write modified PE
    with open(filepath, 'wb') as f:
        f.write(data)

    print(f"Added exports: {[n for n, _ in new_entries]}")
    print(f"Total exports now: {new_num_names}")


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: patch_exports.py <dll_path>")
        sys.exit(1)

    dll_path = sys.argv[1]
    extra = [
        ("_RVExtensionVersion@8", "RVExtensionVersion"),
        ("_RVExtension@12", "RVExtension"),
        ("_RVExtensionArgs@20", "RVExtensionArgs"),
    ]
    patch_pe_exports(dll_path, extra)
