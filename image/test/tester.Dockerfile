# applpi-os-tester: unpacks the image and boots it in QEMU (qemu-virt.sh unpack and boot)
FROM debian:trixie

RUN apt-get -y update \
    && apt-get -y install --no-install-recommends qemu-system-arm mtools openssh-client fdisk xz-utils \
    && rm -rf /var/lib/apt/lists/*
