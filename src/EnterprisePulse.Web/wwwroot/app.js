window.ep = {
  download: async (fileName, streamRef) => {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer]));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName ?? '';
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
  print: () => window.print(),
};
