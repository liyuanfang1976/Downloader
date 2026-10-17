const HOST_NAME = 'com.downloader.host';

chrome.downloads.onCreated.addListener(async item => {
  const target = item.finalUrl || item.url;
  if (!target || !/^https?:/i.test(target)) return;

  await safe(() => chrome.downloads.pause(item.id));
  const headers = await buildHeaders(target, item.referrer);
  const handed = await handOff(item, target, headers);

  if (handed) {
    await safe(() => chrome.downloads.cancel(item.id));
    await safe(() => chrome.downloads.erase({ id: item.id }));
  } else {
    await safe(() => chrome.downloads.resume(item.id));
  }
});

async function buildHeaders(target, referrer) {
  const headers = {};
  if (referrer) headers.Referer = referrer;
  try {
    const cookies = await chrome.cookies.getAll({ url: target });
    if (cookies.length) {
      headers.Cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
    }
  } catch {}
  return headers;
}

async function handOff(item, target, headers) {
  try {
    const reply = await chrome.runtime.sendNativeMessage(HOST_NAME, {
      url: target,
      fileName: baseName(item.filename),
      headers: Object.keys(headers).length > 0 ? headers : undefined
    });
    return !!(reply && reply.ok);
  } catch {
    return false;
  }
}

function baseName(path) {
  if (!path) return undefined;
  const parts = String(path).split(/[\\/]/);
  return parts[parts.length - 1] || undefined;
}

async function safe(fn) {
  try {
    await fn();
  } catch {}
}
