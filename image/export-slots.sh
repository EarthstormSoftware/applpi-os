# Sourced by pi-gen's export-image/05-finalise (build-in-container.sh adds it) once the boot partition is complete,
# after the last update-initramfs. The OS's own boot files (kernels, initramfs, device trees, overlays, cmdline.txt)
# move into slot A, picked by os_prefix in config.txt, so an OS update can write another set into slot B. The
# firmware (bootcode.bin, start*.elf, fixup*.dat) and config.txt stay shared.

boot="${ROOTFS_DIR}/boot/firmware"
install -d "${boot}/slotA"
mv "${boot}"/kernel*.img "${boot}"/initramfs* "${boot}"/*.dtb "${boot}/overlays" "${boot}/cmdline.txt" "${boot}/slotA/"
cat >> "${boot}/config.txt" <<- EOF

	# The OS slot: its kernel, initramfs, device trees, overlays and cmdline.txt. An OS update tries the other slot
	# with tryboot.txt and makes it permanent here once it's running.
	os_prefix=slotA/
EOF
