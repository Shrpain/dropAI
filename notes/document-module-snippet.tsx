import React, { useState, useEffect, useRef } from 'react';
import { Search, Upload, Download, Eye, FileText, X, CheckCircle, MessageSquare, ThumbsUp, Loader2, Sparkles, ArrowLeft } from 'lucide-react';
import { Document, DocumentType } from '../types';
import AIChatPanel from './AIChatPanel';
import { extractTextFromPdf } from '../services/aiService';
import api from '../services/api';
import { supabase } from '../services/supabase';

// Backend DocumentDto mapped to Frontend Document interface
interface DocumentApiResponse {
  id: string;
  title: string;
  description?: string;
  fileUrl: string;
  uploaderId: string;
  uploaderName: string;
  uploaderPublicId?: string;
  uploaderAvatar?: string;
  school?: string;
  subject?: string;
  type?: string;
  year?: string;
  views: number;
  downloads: number;
  createdAt: string;
}

const mapApiDocumentToDocument = (doc: DocumentApiResponse): Document => ({
  id: doc.id,
  title: doc.title || 'Không có tiêu đề',
  school: doc.school || 'Chưa rõ',
  subject: doc.subject || 'Không xác định',
  type: doc.type || 'Tài liệu tham khảo',
  year: doc.year || '',
  uploader: doc.uploaderName,
  uploaderId: doc.uploaderId,
  uploaderPublicId: doc.uploaderPublicId,
  uploaderAvatar: doc.uploaderAvatar,
  createdAt: doc.createdAt,
  views: doc.views,
  downloads: doc.downloads,
  status: 'active',
  description: doc.description,
  fileUrl: doc.fileUrl,
});

const DocumentModule: React.FC = () => {
  const [documents, setDocuments] = useState<Document[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeType, setActiveType] = useState<string>('Tất cả');
  const [showUpload, setShowUpload] = useState(false);
  const [selectedDoc, setSelectedDoc] = useState<Document | null>(null);
  const [uploadSuccess, setUploadSuccess] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');
  const [schoolFilter, setSchoolFilter] = useState('Tất cả');

  // AI Chat States
  const [showAIChat, setShowAIChat] = useState(false);
  const [extractedText, setExtractedText] = useState('');
  const [isExtracting, setIsExtracting] = useState(false);
  const [extractProgress, setExtractProgress] = useState('');

  // Upload form state
  const [uploadTitle, setUploadTitle] = useState('');
  const [uploadType, setUploadType] = useState(DocumentType.EXAM);
  const [uploadSchool, setUploadSchool] = useState('ĐH Khoa học Huế');
  const [uploadYear, setUploadYear] = useState('');
  const [uploadSubject, setUploadSubject] = useState('');
  const [uploadDescription, setUploadDescription] = useState('');
  const [uploadFileUrl, setUploadFileUrl] = useState('');
  const [uploadFile, setUploadFile] = useState<File | null>(null);
  const [uploadMethod, setUploadMethod] = useState<'file' | 'link'>('file');
  const [uploadLoading, setUploadLoading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);

  useEffect(() => {
    fetchDocuments();
  }, []);

  // Realtime subscription for documents
  useEffect(() => {
    const handleRefresh = () => {
      console.log('[DocumentModule] Received refresh event, fetching documents...');
      fetchDocuments();
    };
    window.addEventListener('REFRESH_DOCUMENTS', handleRefresh);

    const channel = supabase
      .channel('public_documents_changes')
      .on(
        'postgres_changes',
        { event: '*', schema: 'public', table: 'documents' },
        (payload) => {
          console.log('[DocumentModule] Realtime change detected:', payload);
          fetchDocuments();
        }
      )
      .subscribe();

    return () => {
      window.removeEventListener('REFRESH_DOCUMENTS', handleRefresh);
      supabase.removeChannel(channel);
    };
  }, []);

  const fetchDocuments = async () => {
    try {
      setLoading(true);
      const response = await api.get<DocumentApiResponse[]>('/documents');
      const mapped = response.data.map(mapApiDocumentToDocument);
      setDocuments(mapped);
      setError(null);
    } catch (err) {
      console.error('Failed to fetch documents:', err);
      setError('Không thể tải danh sách tài liệu');
    } finally {
      setLoading(false);
    }
  };

  const handleDownloadDocument = async (doc: Document) => {
    try {
      await api.post(`/documents/${doc.id}/download`);
      if (doc.fileUrl) {
        window.open(doc.fileUrl, '_blank');
      }
      fetchDocuments();
    } catch (err) {
      console.error('Download error:', err);
    }
  };

  const handleUploadSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setUploadLoading(true);
    setUploadError(null);

    try {
      let finalFileUrl = uploadFileUrl;

      if (uploadMethod === 'file' && uploadFile) {
        const fileExt = uploadFile.name.split('.').pop();
        const fileName = `${Date.now()}.${fileExt}`;
        const { error: uploadErr } = await supabase.storage
          .from('documents')
          .upload(fileName, uploadFile);

        if (uploadErr) throw uploadErr;

        const { data: urlData } = supabase.storage.from('documents').getPublicUrl(fileName);
        finalFileUrl = urlData.publicUrl;
      }

      await api.post('/documents', {
        title: uploadTitle,
        description: uploadDescription,
        fileUrl: finalFileUrl,
        school: uploadSchool,
        subject: uploadSubject,
        type: uploadType,
        year: uploadYear,
      });

      setUploadSuccess(true);
      setTimeout(() => {
        setUploadSuccess(false);
        setShowUpload(false);
        resetUploadForm();
        fetchDocuments();
      }, 2000);
    } catch (err: any) {
      setUploadError(err.message || 'Lỗi khi tải tài liệu');
    } finally {
      setUploadLoading(false);
    }
  };

  const resetUploadForm = () => {
    setUploadTitle('');
    setUploadDescription('');
    setUploadFileUrl('');
    setUploadFile(null);
    setUploadSubject('');
    setUploadYear('');
  };

  const handleAskAI = async () => {
    if (!selectedDoc) return;
    setShowAIChat(true);
    setExtractedText('');

    // Extract text from PDF
    if (selectedDoc.fileUrl) {
      setIsExtracting(true);
      setExtractProgress('Đang đọc tài liệu...');
      try {
        const text = await extractTextFromPdf(selectedDoc.fileUrl, (status) => setExtractProgress(status));
        setExtractedText(text);
      } catch (err) {
        console.error('Extraction failed:', err);
        setExtractedText('');
      } finally {
        setIsExtracting(false);
      }
    }
  };

  const filteredDocuments = documents.filter((doc) => {
    const matchesType = activeType === 'Tất cả' || doc.type === activeType;
    const matchesSearch =
      doc.title.toLowerCase().includes(searchTerm.toLowerCase()) ||
      doc.subject?.toLowerCase().includes(searchTerm.toLowerCase());
    const matchesSchool = schoolFilter === 'Tất cả' || doc.school === schoolFilter;
    return matchesType && matchesSearch && matchesSchool;
  });

  const uniqueSchools = ['Tất cả', ...new Set(documents.map((d) => d.school).filter(Boolean))];

  return (
    <div className="space-y-6 animate-in slide-in-from-bottom-4 duration-500">
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-800">Tài liệu & Đề thi</h1>
          <p className="text-slate-500 text-sm">Tìm kiếm hàng nghìn tài liệu học tập chất lượng tại Huế</p>
        </div>
        <button
          onClick={() => setShowUpload(true)}
          className="bg-teal-600 text-white px-5 py-2.5 rounded-xl font-bold flex items-center justify-center gap-2 hover:bg-teal-700 transition-all shadow-lg shadow-teal-100"
        >
          <Upload size={18} />
          Đóng góp tài liệu
        </button>
      </div>

      {/* Filters */}
      <div className="bg-white p-4 rounded-2xl border shadow-sm space-y-4">
        <div className="flex flex-wrap gap-2">
          {['Tất cả', ...Object.values(DocumentType)].map((type) => (
            <button
              key={type}
              onClick={() => setActiveType(type)}
              className={`px-4 py-1.5 rounded-full text-sm font-medium transition-all ${activeType === type ? 'bg-teal-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'
                }`}
            >
              {type}
            </button>
          ))}
        </div>
        <div className="flex flex-col md:flex-row gap-4">
          <div className="flex-1 relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" size={18} />
            <input
              type="text"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              placeholder="Tên tài liệu, môn học..."
              className="w-full bg-slate-50 border border-slate-200 rounded-xl py-2.5 pl-10 pr-4 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
            />
          </div>
          <select
            value={schoolFilter}
            onChange={(e) => setSchoolFilter(e.target.value)}
            className="bg-slate-50 border border-slate-200 rounded-xl py-2.5 px-4 text-sm focus:ring-2 focus:ring-teal-500 outline-none"
          >
            {uniqueSchools.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* Documents Table */}
      <div className="bg-white rounded-2xl border shadow-sm overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-12">
            <Loader2 className="animate-spin text-teal-600" size={32} />
          </div>
        ) : error ? (
          <div className="text-center py-12 text-red-500">{error}</div>
        ) : filteredDocuments.length === 0 ? (
          <div className="text-center py-12 text-slate-400">Không tìm thấy tài liệu nào</div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left">
              <thead className="bg-slate-50 border-b">
                <tr>
                  <th className="px-6 py-4 text-xs font-bold text-slate-500 uppercase">Tài liệu</th>
                  <th className="px-6 py-4 text-xs font-bold text-slate-500 uppercase">Trường</th>
                  <th className="px-6 py-4 text-xs font-bold text-slate-500 uppercase hidden md:table-cell">Người đăng</th>
                  <th className="px-6 py-4 text-xs font-bold text-slate-500 uppercase text-center">Thống kê</th>
                  <th className="px-6 py-4 text-xs font-bold text-slate-500 uppercase text-right">Hành động</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {filteredDocuments.map((doc) => (
                  <tr
                    key={doc.id}
                    className="hover:bg-slate-50/80 transition-colors cursor-pointer"
                    onClick={() => {
                      setSelectedDoc(doc);
                      setShowAIChat(false);
                      setExtractedText('');
                    }}
                  >
                    <td className="px-6 py-4">
                      <div className="flex items-center gap-3">
                        <div className="w-10 h-10 bg-teal-50 text-teal-600 rounded-lg flex items-center justify-center flex-shrink-0">
                          <FileText size={20} />
                        </div>
                        <div>
                          <p className="text-sm font-bold text-slate-800 line-clamp-1">{doc.title}</p>
                          <p className="text-xs text-slate-500">
                            {doc.subject} • {doc.type}
                          </p>
                        </div>
                      </div>
                    </td>
                    <td className="px-6 py-4">
                      <span className="text-sm text-slate-600">{doc.school}</span>
                    </td>
                    <td className="px-6 py-4 hidden md:table-cell">
                      <div className="flex items-center gap-2">
                        <img
                          src={doc.uploaderAvatar || `https://ui-avatars.com/api/?name=${encodeURIComponent(doc.uploader || 'U')}&background=0d9488&color=fff`}
                          className="w-6 h-6 rounded-full"
                          alt=""
                        />
                        <span className="text-xs text-slate-600 font-medium">{doc.uploader}</span>
                      </div>
                    </td>
                    <td className="px-6 py-4">
                      <div className="flex items-center justify-center gap-4 text-slate-400">
                        <div className="flex flex-col items-center">
                          <span className="text-xs font-bold text-slate-700">{doc.views}</span>
                          <Eye size={14} />
                        </div>
                        <div className="flex flex-col items-center">
                          <span className="text-xs font-bold text-slate-700">{doc.downloads}</span>
                          <Download size={14} />
                        </div>
                      </div>
                    </td>
                    <td className="px-6 py-4 text-right">
                      <div className="flex items-center justify-end gap-2" onClick={(e) => e.stopPropagation()}>
                        <button
                          className="p-2 text-slate-400 hover:text-teal-600 hover:bg-teal-50 rounded-lg transition-all"
                          onClick={() => {
                            setSelectedDoc(doc);
                            setShowAIChat(false);
                          }}
                        >
                          <Eye size={18} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Document Detail Modal - FIXED HEIGHT LAYOUT */}
      {selectedDoc && (
        <div style={{ position: 'fixed', inset: 0, zIndex: 50, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '1rem' }}>
          <div style={{ position: 'absolute', inset: 0, background: 'rgba(15,23,42,0.6)', backdropFilter: 'blur(8px)' }} onClick={() => setSelectedDoc(null)} />

          {/* Modal Container - FIXED HEIGHT 85vh */}
          <div style={{ position: 'relative', background: 'white', width: '100%', maxWidth: '1280px', height: '85vh', borderRadius: '2.5rem', overflow: 'hidden', display: 'flex', flexDirection: 'row', boxShadow: '0 25px 50px -12px rgba(0,0,0,0.25)' }}>

            {/* Left - PDF Preview - FIXED HEIGHT */}
            <div style={{ flex: 1, display: 'flex', flexDirection: 'column', background: '#f1f5f9', height: '100%', overflow: 'hidden' }}>
              {/* Header */}
              <div style={{ padding: '1rem', borderBottom: '1px solid #e2e8f0', background: 'white', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                <div className="flex items-center gap-3">
                  <div className="w-8 h-8 bg-teal-50 rounded-lg flex items-center justify-center text-teal-600">
                    <FileText size={18} />
                  </div>
                  <h3 className="font-bold text-slate-800 line-clamp-1">{selectedDoc.title}</h3>
                </div>
                <button className="md:hidden p-2" onClick={() => setSelectedDoc(null)}>
                  <X size={20} />
                </button>
              </div>

              {/* PDF iframe - FILL REMAINING HEIGHT */}
              <div style={{ flex: 1, overflow: 'hidden' }}>
                {selectedDoc.fileUrl?.toLowerCase().endsWith('.pdf') ? (
                  <iframe src={selectedDoc.fileUrl} style={{ width: '100%', height: '100%', border: 'none' }} title={selectedDoc.title} />
                ) : (
                  <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '2rem' }}>
                    <div className="bg-white w-full max-w-md text-center rounded-2xl shadow-sm p-12 space-y-4">
                      <div className="w-20 h-28 mx-auto border-4 border-slate-100 rounded-lg flex items-center justify-center text-slate-300">
                        <FileText size={48} />
                      </div>
                      <p className="text-slate-400 font-medium">Xem trước không khả dụng</p>
                      <a href={selectedDoc.fileUrl} target="_blank" rel="noopener noreferrer" className="inline-block mt-4 bg-teal-600 text-white px-6 py-2.5 rounded-xl font-bold hover:bg-teal-700">
                        Mở file gốc
                      </a>
                    </div>
                  </div>
                )}
              </div>
            </div>

            {/* Right - Sidebar - FIXED HEIGHT 100% */}
            <div style={{ width: '400px', background: 'white', borderLeft: '1px solid #e2e8f0', display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
              {showAIChat ? (
                /* AI Chat View - FIXED HEIGHT */
                <div style={{ flex: 1, display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
                  {/* Header */}
                  <div style={{ flexShrink: 0, padding: '1rem', borderBottom: '1px solid #e2e8f0', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                    <div className="flex items-center gap-2">
                      <button onClick={() => setShowAIChat(false)} className="p-1.5 hover:bg-slate-50 rounded-xl transition-colors text-slate-500">
                        <ArrowLeft size={18} />
                      </button>
                      <h4 className="font-black text-[13px] text-slate-800 uppercase tracking-wider flex items-center gap-2">
                        <Sparkles size={16} className="text-violet-600" /> Trợ lý AI
                      </h4>
                    </div>
                  </div>

                  {/* Chat Content - FILLS REMAINING HEIGHT */}
                  <div style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
                    {isExtracting ? (
                      <div style={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: '2rem', textAlign: 'center' }}>
                        <div className="relative">
                          <div className="absolute inset-0 bg-violet-200 rounded-full animate-ping opacity-20"></div>
                          <div className="bg-violet-100 p-4 rounded-full text-violet-600 relative">
                            <Sparkles size={32} className="animate-pulse" />
                          </div>
                        </div>
                        <div className="mt-4">
                          <h3 className="font-bold text-slate-800">Đang đọc tài liệu</h3>
                          <p className="text-sm text-slate-500 mt-1">{extractProgress}</p>
                        </div>
                      </div>
                    ) : (
                      <AIChatPanel
                        documentTitle={selectedDoc.title}
                        extractedText={extractedText}
                        onClose={() => setShowAIChat(false)}
                      />
                    )}
                  </div>
                </div>
              ) : (
                /* Information View - FIXED HEIGHT */
                <div style={{ flex: 1, display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
                  <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '1.5rem' }} className="space-y-8">
                    <div className="space-y-4">
                      <h4 className="text-[10px] font-black text-slate-400 uppercase tracking-[0.2em] flex items-center gap-2">
                        <CheckCircle size={14} className="text-teal-500" /> Thông tin cơ bản
                      </h4>
                      <div className="space-y-2.5">
                        <div className="flex items-center justify-between text-xs">
                          <span className="text-slate-400 font-bold">Trường học</span>
                          <span className="text-slate-800 font-black">{selectedDoc.school}</span>
                        </div>
                        <div className="flex items-center justify-between text-xs">
                          <span className="text-slate-400 font-bold">Môn học</span>
                          <span className="text-slate-800 font-black">{selectedDoc.subject}</span>
                        </div>
                        <div className="flex items-center justify-between text-xs">
                          <span className="text-slate-400 font-bold">Năm học</span>
                          <span className="text-slate-800 font-black">{selectedDoc.year || '—'}</span>
                        </div>
                        <div className="flex items-center justify-between text-xs">
                          <span className="text-slate-400 font-bold">Ngày đăng</span>
                          <span className="text-slate-800 font-black">{new Date(selectedDoc.createdAt).toLocaleDateString('vi-VN')}</span>
                        </div>
                      </div>
                      <p className="text-xs text-slate-500 leading-relaxed font-medium bg-slate-50 p-3 rounded-xl border border-slate-100 italic">
                        {selectedDoc.description || 'Không có mô tả cho tài liệu này.'}
                      </p>
                    </div>

                    <div className="space-y-4">
                      <h4 className="text-[10px] font-black text-slate-400 uppercase tracking-[0.2em] flex items-center gap-2">
                        <MessageSquare size={14} className="text-teal-500" /> Thảo luận
                      </h4>
                      <div className="text-sm text-slate-400 italic text-center py-4">Chưa có bình luận nào.</div>
                    </div>
                  </div>

                  {/* Footer Buttons - FIXED AT BOTTOM */}
                  <div style={{ flexShrink: 0, padding: '1.5rem', borderTop: '1px solid #e2e8f0', background: 'white' }} className="space-y-4">
                    <div className="flex gap-3">
                      <button
                        onClick={handleAskAI}
                        className="flex-1 bg-violet-600 text-white py-3 rounded-2xl font-black flex items-center justify-center gap-2 hover:bg-violet-700 shadow-xl shadow-violet-100 transition-all active:scale-95"
                      >
                        <Sparkles size={18} />
                        Hỏi AI
                      </button>
                      <button
                        onClick={() => handleDownloadDocument(selectedDoc)}
                        className="flex-1 bg-teal-600 text-white py-3 rounded-2xl font-black flex items-center justify-center gap-2 hover:bg-teal-700 shadow-xl shadow-teal-100 transition-all active:scale-95"
                      >
                        <Download size={18} />
                        Tải về
                      </button>
                    </div>

                    <div className="flex items-center justify-around text-slate-400 border-t border-slate-100 pt-4">
                      <button className="flex items-center gap-2 hover:text-teal-600 transition-colors group">
                        <div className="p-2 rounded-xl group-hover:bg-teal-50 transition-colors">
                          <ThumbsUp size={18} />
                        </div>
                        <span className="text-sm font-black">0</span>
                      </button>
                      <button className="flex items-center gap-2 hover:text-teal-600 transition-colors group">
                        <div className="p-2 rounded-xl group-hover:bg-teal-50 transition-colors">
                          <MessageSquare size={18} />
                        </div>
                        <span className="text-sm font-black">0</span>
                      </button>
                    </div>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Upload Modal */}
      {showUpload && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
          <div className="absolute inset-0 bg-slate-900/60 backdrop-blur-sm" onClick={() => setShowUpload(false)} />
          <div className="relative bg-white w-full max-w-2xl rounded-[2.5rem] shadow-2xl overflow-hidden animate-in zoom-in-95 duration-200">
            <div className="p-6 border-b flex items-center justify-between">
              <h2 className="text-xl font-black text-slate-800">Đóng góp tài liệu mới</h2>
              <button onClick={() => setShowUpload(false)} className="p-2 hover:bg-slate-100 rounded-full text-slate-400 transition-colors">
                <X size={20} />
              </button>
            </div>
            {uploadSuccess ? (
              <div className="p-12 flex flex-col items-center text-center space-y-4">
                <div className="w-16 h-16 bg-teal-50 text-teal-600 rounded-full flex items-center justify-center">
                  <CheckCircle size={40} />
                </div>
                <h3 className="text-2xl font-black text-slate-800">Tải lên thành công!</h3>
                <p className="text-slate-500 font-medium">Tài liệu của bạn đã được lưu. Cảm ơn bạn đã đóng góp!</p>
              </div>
            ) : (
              <form onSubmit={handleUploadSubmit} className="p-8 space-y-6 overflow-y-auto max-h-[75vh]">
                {uploadError && <div className="bg-red-50 text-red-600 p-3 rounded-xl text-sm">{uploadError}</div>}

                <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                  <div className="space-y-2">
                    <label className="text-xs font-black text-slate-400 uppercase tracking-widest px-1">Tên tài liệu</label>
                    <input
                      type="text"
                      required
                      value={uploadTitle}
                      onChange={(e) => setUploadTitle(e.target.value)}
                      placeholder="Ví dụ: Đề thi Toán A1"
                      className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-xs font-black text-slate-400 uppercase tracking-widest px-1">Loại tài liệu</label>
                    <select
                      value={uploadType}
                      onChange={(e) => setUploadType(e.target.value as DocumentType)}
                      className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
                    >
                      {Object.values(DocumentType).map((v) => (
                        <option key={v} value={v}>
                          {v}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="space-y-2">
                    <label className="text-xs font-black text-slate-400 uppercase tracking-widest px-1">Môn học</label>
                    <input
                      type="text"
                      value={uploadSubject}
                      onChange={(e) => setUploadSubject(e.target.value)}
                      placeholder="Ví dụ: Giải tích 1"
                      className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-xs font-black text-slate-400 uppercase tracking-widest px-1">Năm học</label>
                    <input
                      type="text"
                      value={uploadYear}
                      onChange={(e) => setUploadYear(e.target.value)}
                      placeholder="2024"
                      className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
                    />
                  </div>
                </div>

                <div className="space-y-2">
                  <label className="text-xs font-black text-slate-400 uppercase tracking-widest px-1">Mô tả</label>
                  <textarea
                    value={uploadDescription}
                    onChange={(e) => setUploadDescription(e.target.value)}
                    placeholder="Mô tả ngắn về tài liệu..."
                    rows={3}
                    className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none resize-none"
                  />
                </div>

                <div className="flex gap-2 mb-4">
                  <button type="button" onClick={() => setUploadMethod('file')} className={`flex-1 py-2 rounded-xl text-sm font-bold ${uploadMethod === 'file' ? 'bg-teal-600 text-white' : 'bg-slate-100'}`}>
                    Tải file
                  </button>
                  <button type="button" onClick={() => setUploadMethod('link')} className={`flex-1 py-2 rounded-xl text-sm font-bold ${uploadMethod === 'link' ? 'bg-teal-600 text-white' : 'bg-slate-100'}`}>
                    Dán link
                  </button>
                </div>

                {uploadMethod === 'file' ? (
                  <div
                    className="border-2 border-dashed border-slate-200 rounded-3xl p-10 text-center hover:border-teal-400 hover:bg-teal-50 transition-all cursor-pointer group"
                    onClick={() => document.getElementById('file-upload')?.click()}
                  >
                    <input id="file-upload" type="file" className="hidden" accept=".pdf,.doc,.docx,.ppt,.pptx" onChange={(e) => setUploadFile(e.target.files?.[0] || null)} />
                    {uploadFile ? (
                      <div className="space-y-2">
                        <FileText className="mx-auto text-teal-600" size={40} />
                        <p className="font-bold text-slate-800">{uploadFile.name}</p>
                        <p className="text-xs text-slate-400">{(uploadFile.size / 1024 / 1024).toFixed(2)} MB</p>
                      </div>
                    ) : (
                      <>
                        <Upload className="mx-auto text-slate-200 group-hover:text-teal-500 mb-4 transition-colors" size={40} />
                        <p className="text-sm font-black text-slate-600">Click hoặc kéo thả file</p>
                        <p className="text-xs text-slate-400 mt-2 font-medium">Hỗ trợ: PDF, DOCX, PPTX (Tối đa 50MB)</p>
                      </>
                    )}
                  </div>
                ) : (
                  <div className="space-y-2">
                    <input
                      type="url"
                      value={uploadFileUrl}
                      onChange={(e) => setUploadFileUrl(e.target.value)}
                      placeholder="https://drive.google.com/..."
                      className="w-full bg-slate-50 border-none rounded-2xl px-5 py-3.5 text-sm focus:ring-2 focus:ring-teal-500 transition-all outline-none"
                    />
                    <p className="text-xs text-slate-400">Dán link từ Google Drive, Dropbox...</p>
                  </div>
                )}

                <button
                  type="submit"
                  disabled={uploadLoading}
                  className="w-full bg-teal-600 text-white py-4 rounded-2xl font-black shadow-xl shadow-teal-100 hover:bg-teal-700 transition-all active:scale-95 disabled:opacity-50"
                >
                  {uploadLoading ? <Loader2 className="animate-spin mx-auto" size={20} /> : 'Tải tài liệu lên ngay'}
                </button>
              </form>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

export default DocumentModule;
