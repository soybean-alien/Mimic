# Start here: Mimic in small steps

Mimic watches a video for you and writes down what was said, with a title for each part. It can also look at a picture and describe it. Everything stays on your own computer.

This page walks you through it one small step at a time. You do not need to know anything about computers. In the folder names below, **Username** means your own Windows name.

## Part 1: Put Mimic on your computer

1. Find the file named **Mimic_0.3.2_x64-setup.exe**. It is small (about 7 MB).
2. Double-click it.
3. If Windows shows a blue box that says it protected your PC, click **More info**, then **Run anyway**. This appears because Mimic is a small personal project.
4. Click **Next**, then **Install**.
5. When it says it is done, leave **Open Mimic** ticked and click **Finish**. Mimic opens, and a Mimic picture appears on your desktop. Double-click that picture whenever you want Mimic again.

## Part 2: If setup says something is missing

Mimic needs two helper pieces made by Microsoft. Many computers already have them. If yours does, you never see this part.

1. Setup shows a box that says what is missing and asks if it may open Microsoft's download. Click **Yes**.
2. Your web browser opens and a file starts to download. You do not have to choose anything on the page.
3. When the download finishes, open the file and click **Install**. Wait until it says it worked, then close it.
4. Go back to the Mimic setup box that says **Setup is paused** and click **OK**.
5. If setup asks again, one piece is still missing. Do steps 1 to 4 again for it.

Take as long as you like. Setup waits for you and never gives up.

## Part 3: Get the helpers (two clicks)

The first time Mimic opens, it shows a box called **Downloads**. Mimic needs a few helpers to do its job. They are big, so they are not inside the small setup file.

1. Click **Download tools**. Wait for the bar to fill up (about 226 MB).
2. Click **Download Whisper model**. Wait for the bar to fill up (574 MB, so this one takes longer). The Whisper model is the part that listens and turns talking into words. It is optional: without it Mimic still opens and shows notes you already have, but it cannot write new ones from a video.
3. Click **Close**.

You only do this once. Mimic keeps the helpers in `C:\Users\Username\Mimic` and uses them from then on.

**If you would rather download the files yourself**, the Downloads box has a link beside each one. These are the exact files:

- Whisper model: <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin>. Save it in `C:\Users\Username\Mimic\models`.
- yt-dlp: <https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe>. Save it in `C:\Users\Username\Mimic\tools\yt-dlp`.

## Part 4: Make your first note from a video

1. Open the video in your web browser (for example on YouTube).
2. Click the address at the top of the browser and copy it (hold **Ctrl** and press **C**).
3. In Mimic, click in the long box at the top and paste (hold **Ctrl** and press **V**).
4. Click **Transcribe**.
5. Wait. A bar and a timer show that Mimic is working. A long video takes longer.
6. When it finishes, you see the words, split into parts, each with a time and a short title. Click a time to jump to that spot in the video.

## Part 5: Other things you can do

- **A file you already have:** drag a video or sound file onto the Mimic window and let go.
- **A picture:** drag a picture onto the window. Mimic writes a detailed description and gives its opinion on whether a computer made the picture. That opinion is a guess, not a test.
- **A video with no address:** click **Embedded**, then point the crosshair at the video on your screen.
- **Two at once:** Mimic has two tabs, so two jobs can run side by side.
- **Keep your note:** use **Copy**, **Save .md**, **Save .txt**, **Save .srt** or **Export PDF**.
- **Find old notes:** everything you make is listed on the left side. Click one to open it again.

## Part 6: Choose where your library lives

Your library is the list of every note you have made. Mimic keeps it in `C:\Users\Username\Mimic` unless you pick another place.

1. On the left side of Mimic, click **Change library folder…**.
2. Pick the folder you want (for example one on another drive) and click **Select Folder**.
3. Mimic closes and opens again by itself, now using that folder.

Good to know:

- If the folder you pick has no library yet, Mimic copies your current one into it. Your old one is left where it was, untouched.
- Only the library moves. The helpers from Part 3 always stay in `C:\Users\Username\Mimic`, so they keep working.
- Do not change the folder while a job is running. Wait for it to finish first.

## Part 7: Choose who writes the titles (optional)

The Whisper model always does the listening. A second helper writes the short titles and the picture descriptions. Click **Model** to choose it.

- A new copy of Mimic starts on **Default** and is **not signed in to anything**. No account, password or key comes with it.
- Each choice needs something of your own: your own sign-in for Claude or OpenAI, your own key for DeepSeek, or your own server.
- If you do nothing here, Mimic still writes down the words. It only needs this for titles and pictures.

## If something goes wrong

- **Mimic says a tool is missing:** open **Downloads** and do Part 3 again.
- **A download stops halfway:** click the same button again.
- **The words look wrong:** Whisper makes mistakes with music, noise and quiet voices.
- **A video will not download:** click **Update yt-dlp** on the left side, then try again.
- **You want Mimic gone:** open Windows **Settings**, then **Apps**, find **Mimic**, and choose **Uninstall**. Your library, helpers and saved videos stay on the computer. Delete `C:\Users\Username\Mimic` yourself if you want those gone too.
