/**
 * Building blocks for the style prompt. YuE v1's prompt guide asks for genre, instrument, mood, gender and timbre,
 * all five if possible, and recommends its top_200_tags.json for stable results; YuE2's guide adds language (first,
 * as in its examples) and tempo. The tags below are picked from that list, cleaned of duplicates and Chinese
 * spellings; the duet and choir are free descriptions, which YuE2 accepts as well.
 */
export type StyleCategory = 'language' | 'genre' | 'voice' | 'timbre' | 'instruments' | 'mood' | 'tempo'

interface CategoryDefinition {
  key: StyleCategory
  tags: string[]
  /** One of a kind: choosing a tag replaces the category's other one. */
  exclusive?: boolean
}

/** In the order the style lists them, which is also the order new tags are inserted in. */
export const styleCategories: CategoryDefinition[] = [
  {
    key: 'language',
    exclusive: true,
    tags: ['English', 'German', 'Mandarin', 'Cantonese', 'Japanese', 'Korean', 'Spanish', 'French'],
  },
  {
    key: 'genre',
    tags: [
      'pop',
      'dance pop',
      'synthpop',
      'K-pop',
      'rock',
      'pop rock',
      'indie rock',
      'alternative rock',
      'hard rock',
      'progressive rock',
      'post-rock',
      'grunge',
      'J-rock',
      'punk',
      'pop punk',
      'emo',
      'metal',
      'heavy metal',
      'metalcore',
      'industrial',
      'electronic',
      'electro',
      'house',
      'techno',
      'dubstep',
      'eurodance',
      'downtempo',
      'chillout',
      'lounge',
      'ambient',
      '8-bit',
      'hip-hop',
      'rap',
      'trap',
      'grime',
      'R&B',
      'soul',
      'funk',
      'disco',
      'jazz',
      'big band',
      'blues',
      'bossa nova',
      'latin pop',
      'reggaeton',
      'reggae',
      'dancehall',
      'ska',
      'folk',
      'indie folk',
      'country',
      'bluegrass',
      'celtic',
      'singer-songwriter',
      'gospel',
      'classical',
      'opera',
      'orchestral',
      'soundtrack',
      '80s',
      'new wave',
      'shoegaze',
    ],
  },
  {
    key: 'voice',
    exclusive: true,
    tags: ['male vocal', 'female vocal', 'male and female duet', 'choir', 'child vocal'],
  },
  {
    key: 'timbre',
    tags: [
      'bright vocal',
      'airy vocal',
      'warm vocal',
      'mellow vocal',
      'rich vocal',
      'clear vocal',
      'soft vocal',
      'sweet vocal',
      'smooth vocal',
      'breathy vocal',
      'whispery vocal',
      'powerful vocal',
      'soulful vocal',
      'husky vocal',
      'raspy vocal',
      'gritty vocal',
      'raw vocal',
      'deep vocal',
      'high-pitched vocal',
      'soprano vocal',
      'alto vocal',
      'tenor vocal',
      'baritone vocal',
    ],
  },
  {
    key: 'instruments',
    tags: [
      'piano',
      'electric piano',
      'organ',
      'acoustic guitar',
      'electric guitar',
      'bass',
      'synth bass',
      '808 bass',
      'drums',
      'drum machine',
      'percussion',
      'turntables',
      'synthesizer',
      'strings',
      'violin',
      'cello',
      'harp',
      'flute',
      'saxophone',
      'trumpet',
      'trombone',
      'brass',
      'harmonica',
      'accordion',
      'ukulele',
      'banjo',
      'pedal steel guitar',
      'glockenspiel',
      'marimba',
      'sitar',
      'tabla',
      'erhu',
    ],
  },
  {
    key: 'mood',
    tags: [
      'uplifting',
      'inspiring',
      'happy',
      'energetic',
      'powerful',
      'intense',
      'hopeful',
      'romantic',
      'heartfelt',
      'emotional',
      'sensual',
      'intimate',
      'nostalgic',
      'dreamy',
      'calm',
      'relaxing',
      'chill',
      'peaceful',
      'atmospheric',
      'melancholic',
      'sad',
      'dark',
      'dramatic',
      'epic',
      'futuristic',
      'aggressive',
      'rebellious',
      'playful',
      'groovy',
    ],
  },
  {
    key: 'tempo',
    exclusive: true,
    tags: ['60 BPM', '70 BPM', '80 BPM', '90 BPM', '100 BPM', '110 BPM', '120 BPM', '128 BPM', '140 BPM', '170 BPM'],
  },
]

const order = styleCategories.map((category) => category.key)
const byTag = new Map(
  styleCategories.flatMap((category) => category.tags.map((tag) => [tag.toLowerCase(), category.key] as const)),
)

/** The style's comma-separated items; a sentence without commas stays one item. */
export function styleItems(style: string): string[] {
  return style
    .split(',')
    .map((item) => item.trim())
    .filter((item) => item !== '')
}

/** Where a style item belongs; any tempo typed by hand counts, so that a chosen tempo replaces it. */
export function categoryOf(item: string): StyleCategory | null {
  return byTag.get(item.toLowerCase()) ?? (/^\d{2,3}\s*bpm$/i.test(item) ? 'tempo' : null)
}

export function hasTag(style: string, tag: string): boolean {
  const wanted = tag.toLowerCase()
  return styleItems(style).some((item) => item.toLowerCase() === wanted)
}

/** Adds the tag or, if the style has it, removes it. Items the list does not know stay where they are. */
export function toggleTag(style: string, tag: string): string {
  const wanted = tag.toLowerCase()
  const items = styleItems(style)
  if (items.some((item) => item.toLowerCase() === wanted)) {
    return items.filter((item) => item.toLowerCase() !== wanted).join(', ')
  }
  const category = categoryOf(tag)
  if (category === null) {
    return [...items, tag].join(', ')
  }
  const exclusive = styleCategories.find((definition) => definition.key === category)?.exclusive === true
  const kept = exclusive ? items.filter((item) => categoryOf(item) !== category) : items
  // Before the first item of a later category: language first, tempo last, as in YuE2's examples.
  const rank = order.indexOf(category)
  const later = kept.findIndex((item) => {
    const other = categoryOf(item)
    return other !== null && order.indexOf(other) > rank
  })
  const at = category === 'language' ? 0 : later === -1 ? kept.length : later
  return [...kept.slice(0, at), tag, ...kept.slice(at)].join(', ')
}

export function countTags(style: string, category: StyleCategory): number {
  return styleItems(style).filter((item) => categoryOf(item) === category).length
}
