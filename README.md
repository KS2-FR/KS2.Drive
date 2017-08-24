# KS2.Drive
This software allows you to mount a webDAV / AOS server as if it was a local HDD.

It is similar to what webdrive© or netdrive© do.

Usage :
- Install the latest release of WinFSP (https://github.com/billziss-gh/winfsp/releases)
- Run KS2Drive.exe
- Select the drive letter you want your server to been mounted as
- Enter the Webdav/AOS URL of the server
- Select the protocol (Webdav or AOS)
- Enter your credentials
- Hit "Mount"

Note:
AOS is a kind of webDav protocol implemented in Alfresco ECM (http://docs.alfresco.com/5.0/concepts/aos-intro.html)

Important note:
This a a pre-alpha release, there is still lot of work to be done.<br/>
The software architecture may (and certainly will) change in the future.<br/>
Nevertheless, basic operations (open / read/ write / delete / move) are working.<br/>

Based on WinFSP by Bill Zissimopoulos (https://github.com/billziss-gh/winfsp)<br/>
Based on the WebdavClient library by Itay Sagui (https://github.com/saguiitay/WebDAVClient)
