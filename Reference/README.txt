Before publishing the first time, ensure that the WinFsp*_* directory has been
copied to C:\Program Files (x86)\Microsoft SDKs\ClickOnce Bootstrapper\Packages.
After copying, reboot and restart VS2019, and verify that WinFsp is included
among the prerequisites for publication.

When updating to a new release of WinFsp, install a new version of the
WinFsp*_* directory in the same location.  In product.xml, Hash is the SHA
hash of the .msi file, and the Product UUID to use for the IsMsiInstalled check
can be found in HKEY_CLASSES_ROOT\Installer\Dependencies\WinFsp.
