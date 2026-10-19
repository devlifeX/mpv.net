
mpv.net manual
==============

**ENGLISH** | **[简体中文](manual_chs.md)**

Table of contents
-----------------

* [About](#about)
* [Download](#download)
* [Installation](#installation)
* [Support](#support)
* [Config Folder](#config-folder)
* [Input and context menu](#input-and-context-menu)
* [Continuing interrupted playback](#continuing-interrupted-playback)
* [Saving a buffered video](#saving-a-buffered-video)
* [Command Line Interface](#command-line-interface)
* [Terminal](#terminal)
* [mpv.net specific commands](#mpvnet-specific-commands)
* [mpv.net specific options](#mpvnet-specific-options)
* [External Tools](#external-tools)
* [Scripting](#scripting)
* [Extensions](#extensions)
* [Color Theme](#color-theme)
* [Advanced Features](#advanced-features)
* [Differences compared to mpv](#differences-compared-to-mpv)
* [Environment Variables](#environment-variables)
* [user-data](#user-data)
* [Contributing](#contributing)
* [Context Menu Commands](#context-menu)

About
-----

mpv.net is a media player for Windows that has a modern GUI.

The player is based on the popular [mpv](https://mpv.io) media player.
mpv.net is designed to be mpv compatible, almost all mpv features are available,
this means the official [mpv manual](https://mpv.io/manual/master/) applies to mpv.net,
differences are documented in this manual under [Differences compared to mpv](#differences-compared-to-mpv).


Download
--------

[Stable and beta portable and setup via GitHub download](../../../releases)

Successful builds of `main` publish a Windows x64 portable ZIP to Releases.
Extract the entire archive and run `mpvnet.exe`; this package includes the .NET 10 runtime.

[Changelog](changelog.md)


Installation
------------

#### Requirements

1. Windows 10 or higher.
2. [.NET Desktop Runtime 10.0](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for framework-dependent builds; it is bundled in the Windows x64 portable release.

Internet streaming requires:

- Downloading [yt-dlp](https://github.com/yt-dlp/yt-dlp) and adding its folder
  to the [user environment variable PATH](https://www.google.com/search?q=user+environment+variable+PATH).
  Alternativly the yt-dlp executable can be saved in the mpv.net executable folder.
- In case of proxy server usage, [manual configuration](https://github.com/mpvnet-player/mpv.net/issues/401).

#### File Associations

File Associations can be registered using the context menu under `Config > Setup`.

After the file associations were registered, it might still be necessary to change the
default app in the Windows settings.

Another way to register file associations is using Windows File Explorer,
select a media file and select `Open with > Choose another app` in the context menu.

[Open with++](#open-with) can be used to extend the File Explorer context menu
to get menu items for [Play with mpv.net](https://github.com/stax76/OpenWithPlusPlus#play-with-mpvnet) and
[Add to mpv.net playlist](https://github.com/stax76/OpenWithPlusPlus#add-to-mpvnet-playlist).
Alternativly the `Send To` feature of Windows File Explorer can be used.

#### Path environment variable

In order to use mpv.net in a terminal for advanced use cases,
mpv.net can be added to the Path environment variable, it allows
to run mpv.net in a terminal by typing `mpvnet`. The easiest way
to add mpv.net to path is:

`Context Menu > Config > Setup > Add mpv.net to Path environment variable`

For more information see the [terminal section](#terminal).

Support
-------

Before making a support request, please try the newest [beta version](../../../releases) first.

Support can be requested here:

mpv.net bug reports, feature requests and advanced questions:

https://github.com/mpvnet-player/mpv.net/issues

Beginner mpv questions:

https://www.reddit.com/r/mpv

Advanced mpv questions:

https://github.com/mpv-player/mpv/issues

https://github.com/mpv-player/mpv/discussions


Config Folder
-------------

mpv.net searches the config folder at:

1. Folder defined via MPVNET_HOME environment variable.
2. startup\portable_config (startup means the directory containing mpvnet.exe)
3. `%APPDATA%\mpv.net` (`C:\Users\Username\AppData\Roaming\mpv.net`)

The config folder can be easily opened with:

`Context Menu > Config > Open Config Folder`

The most important files and folders in the config folder are:

- `mpv.conf` file containing the mpv configuration.
- `mpvnet.conf` file containing the mpv.net configuration.
- `input.conf` file containing mpv key and mouse input bindings.
- `scripts` folder containing mpv user scripts.
- `script-opts` folder containing user scripts configuration files.


Continuing interrupted playback
-------------------------------

**Continue** is the first item in the right-click context menu, including legacy
custom menus. It resumes healthy paused playback without discarding its buffer.
After an interruption, or when playback is stalled, it reopens the last successfully
checkpointed file/URL at its saved fractional position. After restarting mpv.net,
click Continue to recover the last playback session. It is also available as
`script-message-to mpvnet continue` for custom key bindings.

Recovery checkpoints are written once per second while playback advances, and
immediately after seeking, pausing or changing key playback settings. They include
the original source, mpv's `watch-later-options` (independent of whether watch-later
resume is enabled), volume/mute, speed, selected tracks, subtitle/audio delays,
video adjustments, external tracks, and connection settings such as HTTP headers,
referrer and yt-dlp format. Continue starts playing even if the checkpoint was
paused. A failed connection keeps the checkpoint so Continue can be tried again
after the network returns.

The checkpoint is stored as `continue.json` in the config directory, including
source URLs and any saved HTTP headers. Writes use a flushed temporary file and
atomic replacement; `continue.json.bak` retains the previous complete checkpoint
and is used if the primary file is unreadable. Recovery does not depend on a normal
exit or shutdown handler. Delete both files to forget the saved session.

An abrupt power loss can lose progress since the last completed checkpoint
(normally up to one second, plus any storage/system delay). Exact seeking requires
a seekable source and a reachable server; deleted files, expired direct URLs,
missing external tracks, and live streams without DVR cannot be restored under
every condition. A failed or imprecise restore reports an error and preserves the
checkpoint. This restores playback state, not the previous session's temporary
downloaded cache; reconnecting may need to buffer again.


Saving a buffered video
-----------------------

Use `File > Save Buffered Video...` in the right-click context menu to save a
network video as a Matroska (`.mkv`) file with the audio track selected when the
Save dialog opens. Later track switches do not change that export. Separate audio
and video sources are combined without re-encoding; the selected audio delay is
preserved. The menu command is also available as
`script-message-to mpvnet save-buffered-video` for custom bindings and menus.

You can save before the entire movie is buffered. Complete, verified cached streams
are reused. Missing beginnings, endings, seek gaps, and audio that was not buffered
before a track switch are completed automatically in a background player. When
mpv cannot prove that the selected stream is complete, that stream's source is read
again from the beginning, rather than splicing potentially mismatched cache
fragments. This can download more than the missing intervals, especially when
audio and video share a container. Streams sharing a source are fetched together.
The foreground playback position, pause state and selected tracks remain unchanged.
The original connection headers, cookies, proxy and TLS settings are used.

The export checks cached packet continuity, reads missing sources through EOF,
and verifies that every selected stream keeps its packet count and duration after
muxing. Original source gaps and legitimately shorter audio tracks are preserved.
The resulting file is decoded before reporting success, including checking decoder
error messages even when FFmpeg exits successfully. Bad cached data is fetched
again automatically. Transient download failures
are retried up to three times. Choose a destination in the Save dialog; an existing
destination is replaced only after the complete export is verified. A canceled,
failed or truncated download leaves the destination intact. Closing mpv.net cancels
an active export. Temporary export files are removed after completion or failure.
FFmpeg is included in release packages for validation and lossless muxing. For a
source build, run `src/Tools/update-mpv.ps1` to obtain FFmpeg alongside libmpv, or
provide `ffmpeg` on PATH. No FFprobe executable is required by the application.

Network videos keep their last frame open at the end, including in playlists,
so the buffer can still be saved. Use Next to continue to the next playlist entry.
Stopping, opening another file, or exiting releases the playback buffer; it does
not survive restarting the player. An export already started retains its chosen
source and tracks independently of later foreground playback changes.

Disk caching is enabled by default and the back buffer allows 512 MiB of packet
metadata (typically sufficient for several hours). Video packets occupy disk space
in mpv's cache directory, potentially as much as the entire video, while metadata
still uses RAM. The disk cache is temporary and is removed when the video closes.
These defaults can be overridden in `mpv.conf` with `cache-on-disk` and
`demuxer-max-back-bytes`. Reduced or disabled caching makes saving read more from
the source. mpv's experimental `dump-cache` can temporarily stall playback for a
large cache export. Verification can also take time on long or high-resolution
videos, and saving requires disk space for intermediate files as well as the final
output. A source that stays unavailable must become accessible before saving can
finish successfully.


Input and context menu
----------------------

Global keyboard shortcuts are supported via `global-input.conf` file.

The config folder can be opened from the context menu: `Config > Open Config Folder`

A input and config editor can be found in the context menu under `Config`.

The input test mode can be started via command line: `--input-test`

The input key list can be printed with `--input-keylist`.

mpv input.conf defaults:  
https://github.com/mpv-player/mpv/blob/master/etc/input.conf

mpv input commands:  
https://mpv.io/manual/master/#list-of-input-commands

mpv input options:  
https://mpv.io/manual/master/#input

Before version v7 all bindings and the context menu definition
were defined in the input.conf file, which mpv.net created
in case it didn't exist. This had the disadvantage that mpv.net
lost control over all default bindings and context menu
defaults. This was unfortunate, v7 introduces a new bindings
and context menu design fixing it.

In v7 no input.conf file is created, the default bindings and
context menu is defined internally. input.conf only contains
what is different from the internally defined defaults,
so it works the same as it works in mpv.

For backward compatibility the old input.conf context menu
format with the menu definition using `#menu: ` is still
supported. The new design also allows for a menu customization,
in a sub section called `Custom`. In input.conf it can be
defined like so:

`Ctrl+a  show-text Test  #custom-menu: Test > Test`

Users that have their bindings and context menu customized
before v7 can easily migrate to the new design by deleting
bindings they don't use and remember the shortcut and remove
`#menu:` everywhere, it's important to remove `#menu:`
everywhere in order to enable the new mode/design.


Command Line Interface
----------------------

**mpvnet** [options] [file|URL|PLAYLIST|-]  
**mpvnet** [options] files


mpv properties can be set with the same syntax as mpv, that is:


To enable the border property:

`--border` or `--border=yes`


To disable the border property:

`--no-boder` or `--border=no`


Supported are all mpv properties, they are documented here:

https://mpv.io/manual/master/#properties


mpv has a few non property based switches which are generally not supported in mpv.net.


Terminal
--------

When mpv.net is started from a terminal it will output status,
error and debug messages to the terminal and accept input keys from the terminal.

mpv.net specific commands
-------------------------

`script-message-to mpvnet <command> <arguments>`

### add-to-path
Adds mpv.net to the Path environment variable.

### remove-from-path
Removes mpv.net from the Path environment variable.

### edit-conf-file [mpv.conf|input.conf]
Opens mpv.conf or input.conf in a text editor.

### load-audio
Shows a file browser dialog to open external audio files.

For automatic detection of external audio files based on the file name,
use the mpv option `audio-file-auto`, it can be found it the config dialog:

`Context Menu > Config > Show Config Editor > Audio > audio-file-auto`

### load-sub
Shows a file browser dialog to open external subtitle files.

For automatic detection of external subtitle files based on the file name,
use the mpv option `sub-auto`, it can be found it the config dialog:

`Context Menu > Config > Show Config Editor > Subtitles > sub-auto`

### move-window [left|top|right|bottom|center]
Moves the Window to the screen edge (Alt+Arrow) or center (Alt+BS).

### open-conf-folder
Opens the config folder with Windows File Explorer.

### open-files [\<flags\>]
**append**  
Appends files to the playlist.

Opens a file browser dialog in order to select files to be opened.
The file browser dialog supports multiselect to load multiple files
at once.

Supported are media files and Blu-ray and DVD ISO image files.

### open-optical-media
Shows a folder browser dialog to open a DVD or BD folder.
ISO images don't have to be mounted, but instead can be
opened directly with the open-files command.

### open-clipboard [\<flags\>]
Opens URLs or filepaths from the clipboard,
or files in the file clipboard format.

**append**  
Appends files/URLs to the playlist.

### play-pause
Cycles the pause property. In case the playlist is empty,
the most recent file from the recent files list is loaded.

### reg-file-assoc \<audio|video|image\>
Registers the file associations.

### scale-window \<factor\>
Decreases or increases the Window size.

### shell-execute \<file|URL\>
Shell executes a single file or URL.

### show-about
Shows the about dialog.

### show-audio-devices
Shows available audio devices in a message box.

### show-commands
Shows available [mpv input commands](https://mpv.io/manual/master/#list-of-input-commands).

### show-properties
Shows available [properties](https://mpv.io/manual/master/#properties).

### show-keys
Shows available [input keys](https://mpv.io/manual/master/#options-input-keylist).

### show-protocols
Shows available [protocols](https://mpv.io/manual/master/#options-list-protocols).

### show-decoders
Shows available decoders.

### show-demuxers
Shows available demuxers.

### show-conf-editor
Shows the conf editor.

### show-input-editor
Shows the input editor.

### show-media-info [\<flags\>]
**msgbox**  
Shows media info in a messsage box.

**editor**  
Shows media info in a text editor.

**osd**
Shows media info on screen.

**full**  
Shows fully detailed media info.

**raw**  
Shows media info with raw property names.

### show-menu
Shows the context menu.

### show-profiles
Shows available profiles with a message box.

### show-text \<text\> \<duration\> \<font-size\>
Shows a OSD message with given text, duration and font size.

### window-scale \<factor\>
Works similar as the [window-scale](https://mpv.io/manual/master/#command-interface-window-scale) mpv property.


mpv.net specific options
------------------------

mpv.net specific options can be found in the conf editor searching for 'mpv.net'.

The options are saved in the mpvnet.conf file.

#### --autofit-audio \<integer\>
Initial window height in percent for audio files. Default: 70

#### --autofit-image \<integer\>
Initial window height in percent for image files. Default: 80

#### --queue \<files\>

Adds files to the playlist, requires [--process-instance=single](#--process-instancevalue).
[Open with++](#open-with) can be used to add files to the playlist using File Explorer.

#### --command=\<input command\>

Sends a input command to a running mpv.net instance via command line, for instance
to create global keyboard shortcuts with AutoHotkey. Requires [process-instance=single](#--process-instancevalue).

### Audio

#### --remember-audio-device=\<yes|no\>

Save and restore the audio device chosen in the context menu. Default: yes

#### --remember-volume=\<yes|no\>

Save volume and mute on exit and restore it on start. Default: yes


### Screen

#### --start-size=\<value\>

Setting to remember the window size.

**width-session**  
Width is remembered in the current session.

**width-always**  
Width is always remembered.

**height-session**  
Height is remembered in the current session. Default

**height-always**  
Height is always remembered.

**video**  
Window size is set to video resolution.

**session**  
Window size is remembered in the current session.

**always**  
Window size is always remembered.

#### --minimum-aspect-ratio=\<float\>

Minimum aspect ratio of the window. Useful to force
a wider window and therefore a larger OSC.

#### --minimum-aspect-ratio-audio=\<float\>

Same as minimum-aspect-ratio but used for audio files.

#### --remember-window-position=\<yes|no\>

Save the window position on exit. Default: no

#### --start-threshold=\<milliseconds\>

Threshold in milliseconds to wait for libmpv returning the video
resolution before the window is shown, otherwise default dimensions
are used as defined by autofit and start-size. Default: 1500

### Playback

#### --auto-load-folder=\<yes|no\>

For single files automatically load the entire directory
into the playlist. This option by default is disabled.
The option is deprecated because mpv now has native
support for it using `autocreate-playlist`,
which by default mpv.net sets to `autocreate-playlist=filter`.


### General

#### --menu-syntax=\<value\>

Used menu syntax for defining the context menu in input.conf.
mpv.net by default uses `#menu:`, uosc uses `#!` by default.

#### --process-instance=\<value\>

Defines if more then one mpv.net process is allowed.

Whenever the CTRL key is pressed when files or URLs are opened,
the playlist is not cleared but the files or URLs are appended to the playlist.
This not only works on process startup but in all mpv.net features that open files and URLs.

Multi can alternatively be enabled by pressing the SHIFT key.

**multi**  
Create a new process everytime the shell starts mpv.net.

**single**  
Force a single process everytime the shell starts mpv.net. Default

**queue**  
Force a single process and add files to playlist.

#### --recent-count=\<int\>

Amount of recent files to be remembered. Default: 15

#### --media-info=\<yes|no\>

Usage of the media info library instead of mpv to access media information. Default: yes (mpv.net specific option)

#### --debug-mode=\<yes|no\>

Enable this only when a developer asks for it. Default: no


### UI

#### --language=\<value\>

User interface display language.
mpv.net must be restarted after a change.

Work on the translation is done with transifex:  
https://app.transifex.com/stax76/teams/

#### --dark-mode=\<value\>

Enables a dark theme.

**always**  
Default

**system**  
Available on Windows 10 or higher.

**never**

#### --dark-theme=\<string\>

Color theme used in dark mode. Default: dark

[Color Themes](#color-theme)

#### --light-theme=\<string\>

Color theme used in light mode. Default: light

[Color Themes](#color-theme)


External Tools
--------------

### Play with mpv

In order to play videos from sites such as YouTube the Chrome Extension [Play with mpv](https://chrome.google.com/webstore/detail/play-with-mpv/hahklcmnfgffdlchjigehabfbiigleji) can be used.

Due to Chrome Extensions not being able to start a app, another app that communicates with the extension is required, this app can be downloaded [here](http://www.mediafire.com/file/lezj8lwqt5zf75v/play-with-mpvnet-server.7z/file). The extension works only when the app is running, to have the app always running a link can be created in the auto start folder located at:

`C:\Users\%username%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup`

This will start the app on system start and have it running in the background. When the file association registration of mpv.net was executed then the app should find the location of mpv.net, alternativly the mpv.net folder can be added to the Path environment variable.


### Open With

Alternatively the Chrome/Firefox extension [Open With](../../../issues/119) can be used.


### Open with++

[Open with++](https://github.com/stax76/OpenWithPlusPlus) can be used to extend the File Explorer context menu to get menu items for [Play with mpv.net](https://github.com/stax76/OpenWithPlusPlus#play-with-mpvnet) and [Add to mpv.net playlist](https://github.com/stax76/OpenWithPlusPlus#add-to-mpvnet-playlist).


### External Application Launcher

Videos can be streamed or downloaded easily with the Chrome extension
[External Application Launcher](https://chromewebstore.google.com/detail/external-application-laun/bifmfjgpgndemajpeeoiopbeilbaifdo),
for download (recommended):

path: `wt`

args: `-- powershell -NoLogo -Command "yt-dlp --ignore-errors --download-archive 'C:\External Application Button.txt' --output 'C:\YouTube\%(channel)s - %(title)s.%(ext)s' ('[HREF]' -replace '&list=.+','')"`


Scripting
---------

#### Lua

A very large collection of user scripts can be found in the GitHub repository
[awesome-mpv](https://github.com/stax76/awesome-mpv). 

Lua scripting is documented in the mpv.net wiki [here](https://github.com/mpvnet-player/mpv.net/wiki/Extending-mpv-and-mpv.net-via-Lua-scripting).

#### JavaScript

[mpv JavaScript documentation](https://mpv.io/manual/master/#javascript)


.NET Extensions
---------------

.NET Extensions are located in a subfolder _extensions_ in the config folder,
the filename must have the same name as the directory:

```Text
<config folder>\extensions\ExampleExtension\ExampleExtension.dll
```


Color Theme
-----------

mpv.net supports custom color themes, the definition of the built-in themes can be found at:

[theme.txt](../../../tree/main/src/Resources/theme.txt)


Custom themes can be saved at:

`<conf folder>\theme.conf`

The theme.conf file may contain multiple themes.

In the config editor under UI there are the options dark-theme and
light-theme to define the themes used in dark and in light mode.


Advanced Features
-----------------

### Playback of VapourSynth scripts

vpy files are supported with following mpv.conf configuration:

```
[extension.vpy]
demuxer-lavf-format = vapoursynth
```

Python and VapourSynth must be in the path environment variable.


Differences compared to mpv
---------------------------

mpv.net is designed to work exactly like mpv, there are a few
differences and limitations:

The configuration folder is named `mpv.net` instead of `mpv`:

`C:\Users\username\AppData\Roaming\mpv.net`

### Window Limitations

mpv.net implements an own main window which means only mpv window
features are supported that have an own implementation in mpv.net.

A window free mode is currently not supported, the main window is always
visible, even when mpv.net is started from the terminal and music is played.

For mpv.net it's currently not possible to find out where OSC menus are located,
but there are 3 features that require this information, therefore mpv.net
makes the assumption that near the window borders might be OSC menus. As a result
the following three features, work only when invoked from the center of the window:

1. Window dragging (moving the window with the mouse).
2. Showing the context menu.
3. Auto hiding the mouse cursor.

When the mouse is near a window border, these 3 features are not available.
The dead zone sizes are 10% left, top, right and 22% bottom.

The documentation of mpv's window features can be found here:

https://mpv.io/manual/master/#window


**mpv.net has currently implemented the following window properties:**

- [border](https://mpv.io/manual/master/#options-border)
- [cursor-autohide](https://mpv.io/manual/master/#options-cursor-autohide)
- [fullscreen](https://mpv.io/manual/master/#options-fullscreen)
- [keepaspect-window](https://mpv.io/manual/master/#options-keepaspect-window)
- [ontop](https://mpv.io/manual/master/#options-ontop)
- [screen](https://mpv.io/manual/master/#options-screen)
- [snap-window](https://mpv.io/manual/master/#options-snap-window)
- [title-bar](https://mpv.io/manual/master/#options-title-bar)
- [title](https://mpv.io/manual/master/#options-title)
- [window-maximized](https://mpv.io/manual/master/#options-window-maximized)
- [window-minimized](https://mpv.io/manual/master/#options-window-minimized)
- [window-scale](https://mpv.io/manual/master/#options-window-scale)


**Partly implemented or modified:**

#### --autofit=\<int\>

\<int\> Initial window height in percent. Default: 60

#### --autofit-smaller=\<int\>

\<int\> Minimum window height in percent. Default: 10

#### --autofit-larger=\<int\>

\<int\> Maximum window height in percent. Default: 80

#### --geometry\<x:y\>

Initial window location in percent. Default: 50:50 (centered)

Requires Windows 11, on Windows 10 it works slightly incorrect due to invisible borders.

x=0 docks the window to the left side.  
x=100 docks the window to the right side.  

y=0 docks the window to the top side.  
y=100 docks the window to the bottom side.

#### --title-bar=\<yes|no\>

Shows the window title bar. Default: yes

**mpv.net specific window features:**

mpv.net specific window features are documented in the [screen section](#screen).


### Command Line Limitations

mpv.net supports property based mpv command line options which means it supports
almost all mpv command line options.

What is not supported are non property bases options. Non property based options
need an own implementation in mpv.net, so far implemented are:

--ad=help  
--audio-device=help  
--input-keylist  
--profile=help  
--vd=help  
--version  

### Other Limitations

The mpv property [idle](https://mpv.io/manual/master/#options-idle) can be
used and mpv.net functions accordingly, but Lua scripts always see `idle=yes`
because mpv.net has to set it to function correctly, this is a difficult
to overcome libmpv limitation.


### mpv.net specific options

Options that are specific to mpv.net can be found by entering _mpv.net_
in the search field of the config editor, in the mpv.net manual they are
documented [here](#mpvnet-specific-options).

mpv.net specific options are saved in the file mpvnet.conf and are just
as mpv properties available on the command line.


Environment Variables
---------------------

### MPVNET_HOME

Directory where mpv.net looks for the user configuration.


user-data
---------

Script authors can access the following
[user-data](https://mpv.io/manual/master/#command-interface-user-data) properties:

```
user-data/frontend/name
user-data/frontend/version
user-data/frontend/process-path
```

Contributing
------------

Work on the translation is done with transifex, translators have to create a transifex account:

https://app.transifex.com/stax76/teams/

For translation questions visit:

https://github.com/mpvnet-player/mpv.net/issues/576
