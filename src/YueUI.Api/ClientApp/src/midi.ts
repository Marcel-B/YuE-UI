/**
 * Lets the user pick a MIDI file, e.g. one exported from Logic. The input is made on the spot, so a button anywhere
 * can open the picker; iOS only allows that inside the click.
 */
export function pickMidiFile(picked: (file: File) => void): void {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = '.mid,.midi,audio/midi,audio/x-midi'
  input.addEventListener('change', () => {
    const file = input.files?.[0]
    if (file) {
      picked(file)
    }
  })
  input.click()
}
