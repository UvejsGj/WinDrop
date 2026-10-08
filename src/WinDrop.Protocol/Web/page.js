"use strict";

// The phone page. Everything shown here that came from the PC (its name, the names of
// offered files) is set with textContent, never parsed as HTML.
(() => {
  const $ = (id) => document.getElementById(id);

  const picker = $("picker");
  const chosen = $("chosen");
  const sendButton = $("send-button");
  const status = $("send-status");
  const progress = $("progress");
  const bar = $("bar");

  let pcName = "this PC";
  let files = [];
  let sending = false;
  let retired = false;

  function formatSize(bytes) {
    if (bytes < 1024) return `${bytes} bytes`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  }

  // Shown at the PC's prompt as who is sending. A guess from the browser, nothing more.
  function device() {
    const ua = navigator.userAgent;
    if (/iPhone/.test(ua)) return "iPhone";
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return "iPad";
    if (/Android/.test(ua)) return "Android phone";
    return "Web browser";
  }

  // The picker's visible face is its label, which does not grey out by itself.
  function setPickerEnabled(enabled) {
    picker.disabled = !enabled;
    document.querySelector("label[for=picker]").classList.toggle("disabled", !enabled);
  }

  function setStatus(text, kind) {
    status.textContent = text;
    status.className = kind ? `status ${kind}` : "status";
  }

  function row(name, size, href) {
    const item = document.createElement("li");
    let label;

    if (href) {
      label = document.createElement("a");
      label.href = href;
      label.setAttribute("download", name);
    } else {
      label = document.createElement("span");
      label.className = "name";
    }

    label.textContent = name;

    const sizeLabel = document.createElement("span");
    sizeLabel.className = "size";
    sizeLabel.textContent = formatSize(size);

    item.append(label, sizeLabel);
    return item;
  }

  async function loadInfo() {
    try {
      const response = await fetch("info", { cache: "no-store" });

      // The PC answers, but not to this link: it was replaced with "New link".
      if (response.status === 404) {
        retired = true;
        sendButton.disabled = true;
        setPickerEnabled(false);
        $("receive").hidden = true;
        setStatus("This link no longer works. Scan the code on the PC again.", "bad");
        return;
      }

      if (!response.ok) return;

      const info = await response.json();
      if (typeof info.name === "string" && info.name) pcName = info.name;

      $("pc-name").textContent = pcName;
      document.title = `WinDrop · ${pcName}`;
      for (const element of document.querySelectorAll(".pc")) element.textContent = pcName;

      const offered = Array.isArray(info.offered) ? info.offered : [];
      $("offered").replaceChildren(...offered.map((file) => row(String(file.name), Number(file.size), String(file.url))));
      $("receive").hidden = offered.length === 0;
    } catch {
      // The PC may have closed WinDrop; the page keeps what it last knew.
    }
  }

  function chooseFiles() {
    files = Array.from(picker.files || []);
    chosen.replaceChildren(...files.map((file) => row(file.name, file.size)));

    sendButton.hidden = files.length === 0;
    sendButton.textContent = files.length === 1 ? "Send 1 file" : `Send ${files.length} files`;
    progress.hidden = true;
    setStatus("");
  }

  function finish() {
    sending = false;
    sendButton.disabled = retired;
    setPickerEnabled(!retired);
  }

  function send() {
    if (sending || files.length === 0) return;

    // The list goes first. The PC shows it at its prompt, before any file is read, and
    // then holds the files to it.
    const form = new FormData();
    form.append("from", device());
    form.append("manifest", JSON.stringify(files.map((file) => ({ name: file.name, size: file.size }))));
    for (const file of files) form.append("file", file, file.name);

    const request = new XMLHttpRequest();
    request.open("POST", "upload");

    sending = true;
    sendButton.disabled = true;
    setPickerEnabled(false);
    progress.hidden = false;
    bar.style.width = "0%";
    setStatus(`Waiting for ${pcName} to accept…`);

    request.upload.addEventListener("progress", (event) => {
      if (!event.lengthComputable) return;
      bar.style.width = `${Math.round((event.loaded / event.total) * 100)}%`;
    });

    request.addEventListener("load", () => {
      finish();

      if (request.status === 200) {
        bar.style.width = "100%";
        setStatus(files.length === 1 ? "Sent." : `Sent ${files.length} files.`, "ok");
        files = [];
        picker.value = "";
        chosen.replaceChildren();
        sendButton.hidden = true;
      } else if (request.status === 403) {
        setStatus(`Declined on ${pcName}.`, "bad");
      } else if (request.status === 413) {
        setStatus("That is more than the PC accepts at once.", "bad");
      } else {
        setStatus("It didn't go through. Try again.", "bad");
      }
    });

    request.addEventListener("error", () => {
      finish();
      setStatus(`It didn't go through. It may have been declined on ${pcName}, or the Wi-Fi dropped.`, "bad");
    });

    request.send(form);
  }

  // Clipboard access needs HTTPS, which this page is not, so copying falls back to
  // selecting the text and the older copy command.
  async function copyLink() {
    const field = $("shortcut-url");

    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(field.value);
      } else {
        field.focus();
        field.setSelectionRange(0, field.value.length);
        document.execCommand("copy");
      }

      $("copy").textContent = "Copied";
    } catch {
      field.focus();
      field.setSelectionRange(0, field.value.length);
      $("copy").textContent = "Press and hold to copy";
    }
  }

  $("shortcut-url").value = `${location.origin}${location.pathname.replace(/\/?$/, "/")}upload`;

  picker.addEventListener("change", chooseFiles);
  sendButton.addEventListener("click", send);
  $("copy").addEventListener("click", copyLink);

  loadInfo();

  // The PC can offer new files while the page is open.
  setInterval(() => {
    if (document.visibilityState === "visible" && !sending && !retired) loadInfo();
  }, 4000);
})();
