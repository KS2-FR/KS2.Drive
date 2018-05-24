# KS2.Drive - β
This software allows you to mount a webDAV / AOS server as if it was a local HDD.<br/>

It is similar to what webdrive© or netdrive© do.

The software is intented to be used in conjunction with Alfresco ECM (and tested as such) but should work with any webDAV server.
As every WebDAV implementation is different, should you encounter an issue with a specific webDAV server, please report here.

Usage :
-	Install the latest release of WinFSP (https://github.com/billziss-gh/winfsp/releases). Currently WinFsp 2017.2
-	Download the lastest build of KS² Drive (https://ci.appveyor.com/api/projects/FrKaram/ks2-drive/artifacts/src/bin/KS2Drive.zip)
-	Run KS2Drive.exe
-	Right-click on the icon loaded in the notification area and select "CONFIGURE"
-	Enter the Webdav/AOS URL of the server
-	Select the protocol (Webdav or AOS)
-	Enter your credentials
-	Hit "SAVE"
-	Right-click on the icon loaded in the notification area and select "MOUNT"

Note :
AOS is a kind of webDav protocol implemented in Alfresco ECM (http://docs.alfresco.com/5.0/concepts/aos-intro.html)
Master branch is based on the beta channel<br/>

Next steps :<br/>
- implement a retry-on-error mechanism.<br/>
- monitor writes and flushes so that closing the program alerts the user when writes count <> flush count. This can lead to data loss

<br/>
<br/>

Based on WinFSP by Bill Zissimopoulos (https://github.com/billziss-gh/winfsp)<br/>
Based on the WebdavClient library by Itay Sagui (https://github.com/saguiitay/WebDAVClient)
