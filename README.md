# KS2.Drive
This software allows you to mount a webDAV / AOS server as if it was a local HDD.<br/>
It is similar to what webdrive© or netdrive© do.

The software is intented to be used in conjunction with Alfresco ECM (and tested as such) but should work with any webDAV server.


Usage :
- Install the latest release of WinFSP (https://github.com/billziss-gh/winfsp/releases). Currently WinFsp 2017.2
- Download the lastest build of KS² Drive (https://ci.appveyor.com/api/projects/FrKaram/ks2-drive/artifacts/src/bin/KS2Drive.zip)
- Run KS2Drive.exe
- Select the drive letter you want your server to been mounted as
- Enter the Webdav/AOS URL of the server
- Select the protocol (Webdav or AOS)
- Enter your credentials
- Hit "Mount"

Important note:
This a a beta release with the implementation of a better UI and a "retry on error" mechanism.

Note :
AOS is a kind of webDav protocol implemented in Alfresco ECM (http://docs.alfresco.com/5.0/concepts/aos-intro.html)

<br/>
<br/>
<br/>

Based on WinFSP by Bill Zissimopoulos (https://github.com/billziss-gh/winfsp)<br/>
Based on the WebdavClient library by Itay Sagui (https://github.com/saguiitay/WebDAVClient)
