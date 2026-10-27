# applpi-os-tester: unpacks the image and boots it in QEMU (qemu-virt.sh unpack, boot and setup; curl and openssl
# are for the setup page)
FROM debian:trixie

RUN apt-get -y update \
    && apt-get -y install --no-install-recommends qemu-system-arm mtools openssh-client fdisk xz-utils curl openssl \
    && rm -rf /var/lib/apt/lists/*
