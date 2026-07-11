/**
 * Plain-English explanations for arXiv categories. The server supplies the
 * official code + name; this adds "what that actually means" without losing
 * anything. Exact code first, then the archive prefix (before the dot), then
 * a generic fallback — cross-listed papers can arrive from any corner of
 * arXiv, so full coverage is impossible and the fallback must read fine.
 */

const byCode: Record<string, string> = {
  'cs.LG': 'How machines learn from data — training methods, architectures, generalization.',
  'cs.AI': 'Broad artificial intelligence: reasoning, planning, agents, knowledge.',
  'cs.CL': 'Computers understanding and generating human language (NLP, LLMs).',
  'cs.CV': 'Computer vision — machines interpreting images and video.',
  'cs.CR': 'Security and cryptography: attacks, defenses, privacy, encryption.',
  'cs.SE': 'Software engineering: how code gets built, tested, and maintained.',
  'cs.DB': 'Databases and data management systems.',
  'cs.DC': 'Distributed computing: clusters, consensus, parallel systems.',
  'cs.DS': 'Data structures and algorithms — the theory of efficient computation.',
  'cs.IR': 'Information retrieval: search engines, ranking, recommendation.',
  'cs.HC': 'Human–computer interaction: interfaces and how people use them.',
  'cs.NI': 'Computer networks and internet architecture.',
  'cs.OS': 'Operating systems.',
  'cs.PL': 'Programming languages: design, compilers, type systems.',
  'cs.RO': 'Robotics: perception, control, and manipulation in the real world.',
  'cs.SI': 'Social networks and computational social science.',
  'cs.CY': 'Computers and society: policy, ethics, fairness, safety.',
  'cs.GT': 'Game theory in computation: auctions, mechanisms, strategic agents.',
  'cs.MA': 'Multi-agent systems: many AIs interacting or cooperating.',
  'cs.NE': 'Neural and evolutionary computing.',
  'cs.SD': 'Sound and audio processing.',
  'cs.AR': 'Hardware and computer architecture.',
  'cs.CE': 'Computational science applied to engineering and finance problems.',
  'cs.DL': 'Digital libraries: organizing and accessing scholarly content.',
  'cs.IT': 'Information theory: the mathematics of communication and compression.',
  'cs.LO': 'Logic in computer science: formal verification, proofs.',
  'cs.MM': 'Multimedia systems.',
  'cs.PF': 'Performance measurement and modeling of systems.',
  'cs.SC': 'Symbolic computation and computer algebra.',
  'stat.ML': 'The statistics side of machine learning — theory and methods.',
  'stat.ME': 'Statistical methodology: how to design sound analyses.',
  'stat.AP': 'Statistics applied to real-world domains.',
  'stat.TH': 'Mathematical statistics theory.',
  'stat.CO': 'Statistical computing and simulation methods.',
  'q-fin.CP': 'Computational finance: pricing, simulation, and numerical methods.',
  'q-fin.TR': 'Market microstructure and trading: how orders become prices.',
  'q-fin.PM': 'Portfolio management: allocating capital across assets.',
  'q-fin.RM': 'Financial risk management and measurement.',
  'q-fin.ST': 'Statistical analysis of financial markets and returns.',
  'q-fin.MF': 'Mathematical finance: the formal theory behind pricing and hedging.',
  'q-fin.PR': 'Pricing of derivatives and other securities.',
  'q-fin.GN': 'General quantitative finance.',
  'q-fin.EC': 'Economics topics within quantitative finance.',
  'econ.EM': 'Econometrics: statistical methods for economic data.',
  'econ.GN': 'General economics.',
  'econ.TH': 'Economic theory.',
  'math.OC': 'Optimization and control: finding the best decision under constraints.',
  'math.ST': 'Mathematical statistics.',
  'math.PR': 'Probability theory.',
  'math.NA': 'Numerical analysis: making computation accurate and stable.',
  'math.DS': 'Dynamical systems.',
  'eess.SP': 'Signal processing: extracting information from measurements.',
  'eess.SY': 'Systems and control engineering.',
  'eess.IV': 'Image and video processing.',
  'eess.AS': 'Audio and speech processing.',
  'physics.soc-ph': 'Physics methods applied to social and economic systems.',
  'physics.data-an': 'Data analysis and statistics methods from physics.',
  'physics.comp-ph': 'Computational physics.',
  'astro-ph.CO': 'Cosmology: the origin, structure, and fate of the universe.',
  'astro-ph.IM': 'Instrumentation and methods for astronomy.',
  'cond-mat.dis-nn': 'Disordered systems and neural networks (statistical physics).',
  'cond-mat.stat-mech': 'Statistical mechanics.',
  'quant-ph': 'Quantum physics and quantum computing.',
  'nlin.AO': 'Adaptation and self-organization in complex systems.',
  'q-bio.NC': 'Neurons and cognition: computational neuroscience.',
  'q-bio.QM': 'Quantitative methods in biology.',
}

const byArchive: Record<string, string> = {
  cs: 'Computer science.',
  stat: 'Statistics.',
  math: 'Mathematics.',
  'q-fin': 'Quantitative finance.',
  econ: 'Economics.',
  eess: 'Electrical engineering and signal processing.',
  physics: 'Physics.',
  'astro-ph': 'Astrophysics — space and the universe.',
  'cond-mat': 'Condensed matter physics.',
  'q-bio': 'Quantitative biology.',
  nlin: 'Nonlinear science and complex systems.',
  'gr-qc': 'General relativity and quantum cosmology.',
  'hep-th': 'Theoretical high-energy physics.',
  'hep-ph': 'High-energy particle physics.',
  'hep-ex': 'Experimental particle physics.',
  'nucl-th': 'Nuclear theory.',
  'math-ph': 'Mathematical physics.',
}

export function categoryGloss(code: string): string {
  if (byCode[code]) return byCode[code]
  const archive = code.split('.')[0]
  if (byArchive[archive]) return byArchive[archive]
  return 'An arXiv research category.'
}
