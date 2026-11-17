# Test rig for the Linux build: the .NET runtime-deps image (what a bare
# Linux box has) plus a virtual X display and the tools to drive and
# screenshot the window. Not shipped; see tests\linux\run.ps1.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11-utils xdotool imagemagick fonts-dejavu-core iproute2 \
        libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 libxext6 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*
ENV DISPLAY=:99
WORKDIR /work
