# applpi-os-test-kernel (ARM): Debian's generic ARM kernel and what's needed to make its initramfs with the image's
# overlayroot (qemu-virt.sh kernel). overlayroot's initramfs hook copies cryptsetup and mke2fs in, and its script
# needs busybox, as on the Pi.
FROM debian:trixie

RUN apt-get -y update \
    && apt-get -y install --no-install-recommends linux-image-armmp initramfs-tools cryptsetup-bin e2fsprogs busybox \
    && rm -rf /var/lib/apt/lists/*

# The virtual card and network, loaded in the initramfs; no swap to resume from
RUN printf '%s\n' virtio_mmio virtio_blk virtio_net >> /etc/initramfs-tools/modules \
    && echo RESUME=none > /etc/initramfs-tools/conf.d/resume
