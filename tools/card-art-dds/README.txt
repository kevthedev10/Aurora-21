FIFA 21 PNG -> DDS drag-and-drop converter

Files:
  PNG-to-FIFA21-DDS-PromoFit-v4.ps1
  Drag-PNG-Here-PromoFit-v4.bat

Requires:
  texconv.exe from Microsoft's DirectXTex project (not committed; *.exe is ignored).

Setup:
  Put texconv.exe in this folder (tools\card-art-dds\), or on PATH.

Usage:
  1. Drag a transparent PNG onto Drag-PNG-Here-PromoFit-v4.bat.
  2. Enter the custom card Resource ID when prompted.
     Example:
       50527512
  3. The output will be:
       p50527512.dds

The converter uses:
  180x180
  BC3_UNORM / DXT5
  1 mip level

For UltimateTeam, place the resulting DDS in:
  ultimateteam\runtime-source\ultimateteam21\custom-playerheads\
