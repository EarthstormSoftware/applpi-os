# juggle-os-tester: unpacks the image and boots it in QEMU (qemu-virt.sh unpack, boot and setup; curl and openssl
# are for the setup page, e2fsprogs for the old data partition put on the card)
FROM debian:trixie

RUN apt-get -y update \
    && apt-get -y install --no-install-recommends qemu-system-arm mtools openssh-client fdisk e2fsprogs xz-utils curl openssl \
    && rm -rf /var/lib/apt/lists/*
