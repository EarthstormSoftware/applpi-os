# Sourced by pi-gen's export-image/05-finalise (build-in-container.sh adds it) once the boot partition is complete,
# after the last update-initramfs. The OS's own boot files (kernels, initramfs, device trees, overlays, cmdline.txt)
# move into slot A, picked by os_prefix in config.txt, so an OS update can write another set into slot B. The
# firmware (bootcode.bin, start*.elf, fixup*.dat) and config.txt stay shared.

boot="${ROOTFS_DIR}/boot/firmware"
install -d "${boot}/slotA"
mv "${boot}"/kernel*.img "${boot}"/initramfs* "${boot}"/*.dtb "${boot}/overlays" "${boot}/cmdline.txt" "${boot}/slotA/"
# Each slot says which OS it holds; the copy at the top is the permanent slot's
cp "${boot}/juggle-release.txt" "${boot}/slotA/"
cat >> "${boot}/config.txt" <<- EOF

	# The OS slot: its kernel, initramfs, device trees, overlays and cmdline.txt. An OS update tries the other slot
	# with tryboot.txt and makes it permanent here once it's running.
	os_prefix=slotA/
EOF

# The boot partition can't grow once a Pi is in use, so there must be room for the other slot, and for the slots to
# grow: twice slot A's size free
slot_mib=$(du -sm "${boot}/slotA" | cut -f1)
free_mib=$(df -BM --output=avail "${boot}" | tail -1 | tr -dc 0-9)
echo "Boot partition: slot A is ${slot_mib} MiB, ${free_mib} MiB free"
if [ "${free_mib}" -lt $((slot_mib * 2)) ]; then
	echo "The boot partition is too small: slot B needs ${slot_mib} MiB, and room to grow" >&2
	exit 1
fi
